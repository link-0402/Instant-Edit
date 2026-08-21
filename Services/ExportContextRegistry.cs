using System.Security.Cryptography;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary>
/// In-memory authority for the import/export relationship. The addon can echo a
/// context, but it cannot choose a different target because all target data lives here.
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
        public required InstantEditImportContext Context { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
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
    private readonly Dictionary<string, ContextEntry> _contexts = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private bool _disposed;

    public ExportContextRegistry(string pluginInstanceId, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("A plugin instance id is required.", nameof(pluginInstanceId));

        PluginInstanceId = pluginInstanceId;
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
    }

    public string PluginInstanceId { get; }

    public InstantEditImportContext CreateContext(
        string gamePath,
        int objectIndex,
        string modName,
        int callbackPort,
        ActorIdentity? actorIdentity)
    {
        if (!PenumbraService.IsSafeGamePath(gamePath) || objectIndex is < 0 or > ushort.MaxValue ||
            !PenumbraService.IsSafeModName(modName) || callbackPort is < 1 or > 65535)
            throw new ArgumentException("The import target is not safe.");

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
            ModName = modName,
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

        return context;
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

            byte[] suppliedCapability;
            try
            {
                suppliedCapability = Convert.FromBase64String(capability ?? string.Empty);
            }
            catch (FormatException)
            {
                code = "invalid_capability";
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(entry.Context.Capability), suppliedCapability))
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
        lock (_lock)
            _contexts.Remove(contextId);
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        foreach (var id in _contexts.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _contexts.Remove(id);
    }

    private static bool IsSafeId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

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
