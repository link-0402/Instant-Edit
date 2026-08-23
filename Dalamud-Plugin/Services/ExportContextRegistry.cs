using System.Security.Cryptography;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary>
/// Authority for the import/export relationship. Active contexts are indexed in
/// memory, while their durable records let a saved Blender scene reconnect after
/// a plugin or application restart. The addon can echo a context, but it cannot
/// choose a different target because all target data lives here.
/// </summary>
public sealed class ExportContextRegistry : IDisposable
{
    private sealed class ExportEntry
    {
        public required string FilePath { get; init; }
        public required long Size { get; init; }
        public required string Sha256 { get; init; }
        public required TaskCompletionSource<ExportReceipt> Completion { get; init; }
    }

    private sealed class ContextEntry
    {
        public required InstantEditImportContext Context { get; set; }
        public required DateTimeOffset ExpiresAt { get; set; }
        public Dictionary<string, ExportEntry> Exports { get; } = new(StringComparer.Ordinal);
    }

    public sealed class ExportReservation
    {
        internal ExportReservation(InstantEditImportContext context, Task<ExportReceipt> completion, bool owner)
        {
            Context = context;
            Completion = completion;
            IsOwner = owner;
        }

        public InstantEditImportContext Context { get; }
        public Task<ExportReceipt> Completion { get; }
        public bool IsOwner { get; }
    }

    private readonly object _lock = new();
    private readonly object _persistenceLock = new();
    private readonly Dictionary<string, ContextEntry> _contexts = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly Action<IReadOnlyList<PersistedExportContext>>? _persist;
    private readonly Func<int, ActorIdentity?>? _actorIdentityProvider;
    private bool _disposed;

    public ExportContextRegistry(
        string pluginInstanceId,
        IEnumerable<PersistedExportContext>? persisted = null,
        Action<IReadOnlyList<PersistedExportContext>>? persist = null,
        Func<int, ActorIdentity?>? actorIdentityProvider = null,
        TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("A plugin instance id is required.", nameof(pluginInstanceId));

        PluginInstanceId = pluginInstanceId;
        _lifetime = lifetime ?? TimeSpan.FromDays(30);
        _persist = persist;
        _actorIdentityProvider = actorIdentityProvider;

        var now = DateTimeOffset.UtcNow;
        if (persisted is not null)
        {
            foreach (var saved in persisted)
            {
                if (saved is null || saved.ExpiresAt <= now || !IsSafePersistedContext(saved))
                    continue;

                var context = RuntimeContext(saved, _actorIdentityProvider?.Invoke(saved.ObjectIndex));
                _contexts[context.ContextId] = new ContextEntry
                {
                    Context = context,
                    ExpiresAt = saved.ExpiresAt,
                };
            }
        }
    }

    public string PluginInstanceId { get; }

    public InstantEditImportContext CreateContext(
        string gamePath,
        int objectIndex,
        string modName,
        int callbackPort,
        ActorIdentity? actorIdentity)
        => CreateContext(gamePath, objectIndex, modName, string.Empty, modName, callbackPort, actorIdentity);

    public InstantEditImportContext CreateContext(
        string gamePath,
        int objectIndex,
        string sourceModDirectory,
        string targetFilePath,
        string sourceModName,
        int callbackPort,
        ActorIdentity? actorIdentity,
        string? sourceModRootPath = null)
    {
        if (!PenumbraService.IsSafeGamePath(gamePath) || objectIndex is < 0 or > ushort.MaxValue ||
            !PenumbraService.IsSafeModName(sourceModDirectory) || callbackPort is < 1 or > 65535)
            throw new ArgumentException("The import target is not safe.");

        string targetFolder;
        if (string.IsNullOrEmpty(targetFilePath))
        {
            // Compatibility contexts can still import, but the server will not
            // authorize Quick Export without an original physical destination.
            targetFolder = sourceModDirectory;
        }
        else
        {
            if (!PenumbraService.IsSafeLocalModelPath(targetFilePath))
                throw new ArgumentException("The original model path is not a safe local .mdl file.");
            targetFilePath = Path.GetFullPath(targetFilePath);
            targetFolder = Path.GetDirectoryName(targetFilePath)
                ?? throw new ArgumentException("The original model has no parent directory.");
        }

        if (actorIdentity is not null && actorIdentity.ObjectIndex != objectIndex)
            throw new ArgumentException("The actor identity does not match the object index.");

        var context = new InstantEditImportContext
        {
            PluginInstanceId = PluginInstanceId,
            ContextId = Guid.NewGuid().ToString("N"),
            ImportId = Guid.NewGuid().ToString("N"),
            Capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            GamePath = gamePath,
            ObjectIndex = (ushort)objectIndex,
            ActorIdentity = actorIdentity,
            ModName = sourceModDirectory,
            TargetFilePath = targetFilePath,
            TargetFolder = targetFolder,
            SourceModDirectory = sourceModDirectory,
            SourceModName = string.IsNullOrWhiteSpace(sourceModName) ? sourceModDirectory : sourceModName,
            SourceModRootPath = sourceModRootPath,
            CallbackPort = callbackPort,
        };

        lock (_lock)
        {
            ThrowIfDisposed();
            RemoveExpiredLocked(DateTimeOffset.UtcNow);
            _contexts[context.ContextId] = new ContextEntry
            {
                Context = context,
                ExpiresAt = DateTimeOffset.UtcNow + _lifetime,
            };
        }

        Persist();

        return context;
    }

    public bool TryReattach(
        string contextId,
        string importId,
        string capability,
        int callbackPort,
        out InstantEditImportContext? context,
        out string code)
    {
        context = null;
        code = "stale_context";

        if (callbackPort is < 1 or > 65535)
        {
            code = "malformed_request";
            return false;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                code = "server_stopped";
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            RemoveExpiredLocked(now);
            if (!IsSafeId(contextId) || !_contexts.TryGetValue(contextId, out var entry))
            {
                code = "stale_context";
                return false;
            }

            if (!string.Equals(entry.Context.ImportId, importId, StringComparison.Ordinal))
            {
                code = "stale_context";
                return false;
            }

            if (!CapabilityMatches(entry.Context.Capability, capability))
            {
                code = "invalid_capability";
                return false;
            }

            var refreshed = entry.Context with
            {
                PluginInstanceId = PluginInstanceId,
                ActorIdentity = _actorIdentityProvider?.Invoke(entry.Context.ObjectIndex),
                CallbackPort = callbackPort,
            };
            entry.Context = refreshed;
            entry.ExpiresAt = now + _lifetime;
            context = refreshed;
        }

        Persist();
        code = "context_reattached";
        return true;
    }

    public bool TryBeginExport(
        string pluginInstanceId,
        string contextId,
        string exportId,
        string capability,
        string filePath,
        long size,
        string sha256,
        out ExportReservation? reservation,
        out string code)
    {
        reservation = null;
        code = "invalid_context";

        lock (_lock)
        {
            if (_disposed)
            {
                code = "server_stopped";
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            RemoveExpiredLocked(now);

            if (!string.Equals(pluginInstanceId, PluginInstanceId, StringComparison.Ordinal))
            {
                code = "plugin_instance_mismatch";
                return false;
            }

            if (!IsSafeId(contextId) || !_contexts.TryGetValue(contextId, out var entry))
            {
                code = "stale_context";
                return false;
            }

            if (!CapabilityMatches(entry.Context.Capability, capability))
            {
                code = "invalid_capability";
                return false;
            }

            if (!IsSafeId(exportId))
            {
                code = "malformed_request";
                return false;
            }

            if (entry.Exports.TryGetValue(exportId, out var previous))
            {
                if (string.Equals(previous.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                    previous.Size == size && string.Equals(previous.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    reservation = new ExportReservation(entry.Context, previous.Completion.Task, false);
                    code = "duplicate_export";
                    return true;
                }

                code = "duplicate_export_id";
                return false;
            }

            var completion = new TaskCompletionSource<ExportReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Exports[exportId] = new ExportEntry
            {
                FilePath = filePath,
                Size = size,
                Sha256 = sha256,
                Completion = completion,
            };
            reservation = new ExportReservation(entry.Context, completion.Task, true);
            code = "accepted";
            return true;
        }
    }

    public void CompleteExport(string contextId, string exportId, ExportReceipt receipt)
    {
        lock (_lock)
        {
            if (_contexts.TryGetValue(contextId, out var context) && context.Exports.TryGetValue(exportId, out var export))
                export.Completion.TrySetResult(receipt);
        }
    }

    public void RemoveContext(string contextId)
    {
        var removed = false;
        lock (_lock)
            removed = _contexts.Remove(contextId);
        if (removed)
            Persist();
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        foreach (var id in _contexts.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _contexts.Remove(id);
    }

    private static bool IsSafeId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool CapabilityMatches(string expected, string? supplied)
    {
        try
        {
            var expectedBytes = Convert.FromBase64String(expected);
            var suppliedBytes = Convert.FromBase64String(supplied ?? string.Empty);
            return expectedBytes.Length == 32 &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private InstantEditImportContext RuntimeContext(PersistedExportContext saved, ActorIdentity? actorIdentity)
        => new()
        {
            PluginInstanceId = PluginInstanceId,
            ContextId = saved.ContextId,
            ImportId = saved.ImportId,
            Capability = saved.Capability,
            GamePath = saved.GamePath,
            ObjectIndex = saved.ObjectIndex,
            ActorIdentity = actorIdentity,
            ModName = saved.ModName,
            TargetFilePath = saved.TargetFilePath,
            TargetFolder = saved.TargetFolder,
            SourceModDirectory = saved.SourceModDirectory,
            SourceModName = saved.SourceModName,
            SourceModRootPath = saved.SourceModRootPath,
            CallbackPort = saved.CallbackPort,
        };

    private static bool IsSafePersistedContext(PersistedExportContext saved)
    {
        if (!IsSafeId(saved.ContextId) || !IsSafeId(saved.ImportId) ||
            !CapabilityMatches(saved.Capability, saved.Capability) ||
            !PenumbraService.IsSafeGamePath(saved.GamePath) ||
            !PenumbraService.IsSafeModName(saved.SourceModDirectory) ||
            !PenumbraService.IsSafeLocalModelPath(saved.TargetFilePath) ||
            saved.CallbackPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(saved.SourceModName) || string.IsNullOrWhiteSpace(saved.TargetFolder))
            return false;

        return true;
    }

    private void Persist()
    {
        if (_persist is null)
            return;

        // Keep snapshot creation and the external save callback in one ordered
        // critical section. A later mutation can then never persist before an
        // older snapshot and subsequently be overwritten by it.
        lock (_persistenceLock)
        {
            List<PersistedExportContext> snapshot;
            lock (_lock)
            {
                snapshot = _contexts.Values
                    .Select(entry => PersistedExportContext.FromContext(entry.Context, entry.ExpiresAt))
                    .ToList();
            }

            _persist(snapshot);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExportContextRegistry));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _contexts.Clear();
        }
    }
}
