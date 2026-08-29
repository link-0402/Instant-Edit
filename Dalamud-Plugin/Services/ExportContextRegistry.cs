using System.Security.Cryptography;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary>
/// Authority for the import/export relationship. Active contexts are indexed in
/// memory, while their durable records let a saved Blender scene reconnect after
/// a plugin or application restart. The add-on can echo a context, but it cannot
/// choose a different target because all target data lives here.
/// </summary>
public sealed class ExportContextRegistry : IDisposable
{
    private sealed class ExportEntry
    {
        public required string FilePath { get; init; }
        public required long Size { get; init; }
        public required string Sha256 { get; init; }
        public string? RequestFingerprint { get; init; }
        public required TaskCompletionSource<ExportReceipt> Completion { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
    }

    private sealed class ContextEntry
    {
        public required InstantEditImportContext Context { get; set; }
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
    private readonly Action<IReadOnlyList<PersistedExportContext>>? _persist;
    private static readonly TimeSpan ReceiptRetention = TimeSpan.FromDays(1);
    private const int MaxCompletedReceiptsPerContext = 128;
    private bool _disposed;

    public ExportContextRegistry(
        string pluginInstanceId,
        IEnumerable<PersistedExportContext>? persisted = null,
        Action<IReadOnlyList<PersistedExportContext>>? persist = null)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("A plugin instance id is required.", nameof(pluginInstanceId));

        PluginInstanceId = pluginInstanceId;
        _persist = persist;

        if (persisted is not null)
        {
            foreach (var saved in persisted)
            {
                if (saved is null || !IsSafePersistedContext(saved))
                    continue;

                var context = RuntimeContext(saved);
                _contexts[context.ContextId] = new ContextEntry
                {
                    Context = context,
                };
            }
        }
    }

    public string PluginInstanceId { get; }

    public InstantEditImportContext CreateContext(
        string gamePath,
        int objectIndex,
        string sourceModDirectory,
        string targetFilePath,
        string sourceModName,
        int callbackPort,
        string? sourceModRootPath = null,
        string? targetRelativePath = null,
        ResourceDependencyManifest? resourceManifest = null)
    {
        if (!PenumbraService.IsSafeGamePath(gamePath) || objectIndex is < 0 or > ushort.MaxValue ||
            !PenumbraService.IsSafeModName(sourceModDirectory) || callbackPort is < 1 or > 65535)
            throw new ArgumentException("The import target is not safe.");

        if (!PenumbraService.IsSafeLocalModelPath(targetFilePath))
            throw new ArgumentException("The original model path is not a safe local .mdl file.");
        targetFilePath = Path.GetFullPath(targetFilePath);
        var targetFolder = Path.GetDirectoryName(targetFilePath)
            ?? throw new ArgumentException("The original model has no parent directory.");
        sourceModRootPath = NormalizeRoot(sourceModRootPath);
        targetRelativePath = ResolveTargetRelativePath(
            targetFilePath,
            sourceModRootPath,
            targetRelativePath);

        var safeManifest = IsSafeResourceManifest(resourceManifest) ? resourceManifest : null;
        var context = new InstantEditImportContext
        {
            PluginInstanceId = PluginInstanceId,
            ContextId = Guid.NewGuid().ToString("N"),
            ImportId = Guid.NewGuid().ToString("N"),
            Capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            GamePath = gamePath,
            SourceKind = InstantEditImportContext.ModSource,
            ResolvedGamePath = gamePath,
            DestinationState = InstantEditImportContext.ReadyDestination,
            ObjectIndex = (ushort)objectIndex,
            TargetFilePath = targetFilePath,
            TargetFolder = targetFolder,
            SourceModDirectory = sourceModDirectory,
            SourceModName = string.IsNullOrWhiteSpace(sourceModName) ? sourceModDirectory : sourceModName,
            SourceModRootPath = sourceModRootPath,
            TargetRelativePath = targetRelativePath,
            CallbackPort = callbackPort,
            ResourceManifest = safeManifest,
            ResourceManifestStatus = safeManifest is not null
                ? "ready"
                : "capture_failed",
        };

        lock (_lock)
        {
            ThrowIfDisposed();
            _contexts[context.ContextId] = new ContextEntry
            {
                Context = context,
            };
        }

        Persist();

        return context;
    }

    public InstantEditImportContext CreateGameContext(
        string gamePath,
        string resolvedGamePath,
        int objectIndex,
        int callbackPort,
        Guid? targetCollectionId = null,
        string? targetCollectionName = null,
        ResourceDependencyManifest? resourceManifest = null)
    {
        if (!PenumbraService.IsSafeGamePath(gamePath) ||
            !PenumbraService.IsSafeGamePath(resolvedGamePath) ||
            !gamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            !resolvedGamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            objectIndex is < 0 or > ushort.MaxValue || callbackPort is < 1 or > 65535 ||
            targetCollectionId == Guid.Empty ||
            (targetCollectionName is not null && targetCollectionName.Length > 512))
            throw new ArgumentException("The game-data import target is not safe.");

        var safeManifest = IsSafeResourceManifest(resourceManifest) ? resourceManifest : null;
        var context = new InstantEditImportContext
        {
            PluginInstanceId = PluginInstanceId,
            ContextId = Guid.NewGuid().ToString("N"),
            ImportId = Guid.NewGuid().ToString("N"),
            Capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            GamePath = gamePath,
            SourceKind = InstantEditImportContext.GameSource,
            ResolvedGamePath = resolvedGamePath,
            DestinationState = InstantEditImportContext.NewModRequiredDestination,
            ObjectIndex = (ushort)objectIndex,
            TargetCollectionId = targetCollectionId,
            TargetCollectionName = string.IsNullOrWhiteSpace(targetCollectionName) ? null : targetCollectionName,
            CallbackPort = callbackPort,
            ResourceManifest = safeManifest,
            ResourceManifestStatus = safeManifest is not null ? "ready" : "capture_failed",
        };

        lock (_lock)
        {
            ThrowIfDisposed();
            _contexts[context.ContextId] = new ContextEntry { Context = context };
        }
        Persist();
        return context;
    }

    public bool PromoteGameContext(
        string contextId,
        string sourceModDirectory,
        string sourceModName,
        string sourceModRootPath,
        string targetRelativePath,
        out InstantEditImportContext? context)
    {
        context = null;
        if (!IsSafeId(contextId) || !PenumbraService.IsSafeModName(sourceModDirectory) ||
            string.IsNullOrWhiteSpace(sourceModName) || sourceModName.Length > 512 ||
            !PenumbraService.IsSafeRelativeModelPath(targetRelativePath))
            return false;

        string root;
        string target;
        try
        {
            root = Path.GetFullPath(sourceModRootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            target = Path.GetFullPath(Path.Combine(root, targetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(root) || !File.Exists(target) || !IsPathWithin(target, root) ||
                !PenumbraService.IsSafeLocalModelPath(target))
                return false;
        }
        catch
        {
            return false;
        }

        lock (_lock)
        {
            if (_disposed || !_contexts.TryGetValue(contextId, out var entry) ||
                entry.Context.SourceKind != InstantEditImportContext.GameSource ||
                entry.Context.DestinationState != InstantEditImportContext.NewModRequiredDestination)
                return false;
            context = entry.Context with
            {
                DestinationState = InstantEditImportContext.ReadyDestination,
                SourceModDirectory = sourceModDirectory,
                SourceModName = sourceModName,
                SourceModRootPath = root,
                TargetRelativePath = targetRelativePath.Replace('\\', '/'),
                TargetFilePath = target,
                TargetFolder = Path.GetDirectoryName(target),
            };
            entry.Context = context;
        }
        Persist();
        return true;
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
                // Native actor addresses are process-local redraw hints. Never
                // attach a saved context to whichever actor now occupies the
                // previous object-table index.
                CallbackPort = callbackPort,
            };
            entry.Context = refreshed;
            context = refreshed;
        }

        Persist();
        code = "context_reattached";
        return true;
    }

    /// <summary>
    /// Update durable paths after Penumbra normalization moves files inside a
    /// source mod. The operation is deliberately keyed by Penumbra directory
    /// name so unrelated imported contexts are never rewritten.
    /// </summary>
    public bool RemapModPaths(
        string sourceModDirectory,
        string modRoot,
        IReadOnlyDictionary<string, string> relativePaths)
    {
        if (!PenumbraService.IsSafeModName(sourceModDirectory) ||
            string.IsNullOrWhiteSpace(modRoot) || relativePaths.Count == 0)
            return false;

        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in relativePaths)
        {
            if (!TryNormalizeRelativePath(pair.Key, out var oldPath) ||
                !TryNormalizeRelativePath(pair.Value, out var newPath))
                continue;
            remap[oldPath] = newPath;
            if (oldPath.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
                remap[oldPath[6..]] = newPath;
            else
                remap["Files/" + oldPath] = newPath;
        }
        if (remap.Count == 0)
            return false;

        string root;
        try
        {
            root = Path.GetFullPath(modRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        var changed = false;
        lock (_lock)
        {
            if (_disposed)
                return false;

            foreach (var entry in _contexts.Values)
            {
                var context = entry.Context;
                if (!string.Equals(context.SourceModDirectory, sourceModDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                var updated = context;
                if (TryGetRemappedPath(context.TargetRelativePath, remap, out var relative))
                {
                    var contextRoot = string.IsNullOrWhiteSpace(context.SourceModRootPath)
                        ? root
                        : context.SourceModRootPath!;
                    try
                    {
                        var targetPath = Path.GetFullPath(Path.Combine(
                            contextRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (IsPathWithin(targetPath, contextRoot))
                        {
                            updated = updated with
                            {
                                TargetRelativePath = relative,
                                TargetFilePath = targetPath,
                                TargetFolder = Path.GetDirectoryName(targetPath) ?? updated.TargetFolder,
                            };
                        }
                    }
                    catch
                    {
                        // A malformed remap cannot invalidate an otherwise
                        // usable context; the unchanged context remains safe.
                    }
                }

                if (updated.ResourceManifest is { } manifest)
                {
                    var manifestChanged = false;
                    var materials = manifest.Materials.Select(material =>
                    {
                        var materialResource = RemapLocator(material.Resource, sourceModDirectory, remap, ref manifestChanged);
                        var textures = material.Textures.Select(texture => texture with
                        {
                            Resource = RemapLocator(texture.Resource, sourceModDirectory, remap, ref manifestChanged),
                        }).ToArray();
                        return material with { Resource = materialResource, Textures = textures };
                    }).ToArray();
                    if (manifestChanged)
                        updated = updated with { ResourceManifest = manifest with { Materials = materials } };
                }

                if (!Equals(updated, context))
                {
                    entry.Context = updated;
                    changed = true;
                }
            }
        }

        if (changed)
            Persist();
        return changed;
    }

    private static SourceResourceLocator RemapLocator(
        SourceResourceLocator locator,
        string sourceModDirectory,
        IReadOnlyDictionary<string, string> remap,
        ref bool changed)
    {
        if (!string.Equals(locator.Kind, "mod", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(locator.SourceModDirectory, sourceModDirectory, StringComparison.OrdinalIgnoreCase) ||
            !TryGetRemappedPath(locator.SourceRelativePath, remap, out var relative))
            return locator;
        changed = true;
        return locator with { SourceRelativePath = relative };
    }

    private static bool TryGetRemappedPath(
        string? path,
        IReadOnlyDictionary<string, string> remap,
        out string relative)
    {
        relative = string.Empty;
        if (!TryNormalizeRelativePath(path, out var normalized))
            return false;
        if (!remap.TryGetValue(normalized, out var candidate) || string.IsNullOrWhiteSpace(candidate))
            return false;
        relative = candidate;
        return true;
    }

    private static bool TryNormalizeRelativePath(string? path, out string normalized)
        => PathRules.TryNormalizeRelativePath(path, out normalized);

    private static bool IsPathWithin(string path, string root)
        => PathRules.IsPathWithin(path, root);

    public bool TryBeginExport(
        string pluginInstanceId,
        string contextId,
        string exportId,
        string capability,
        string filePath,
        long size,
        string sha256,
        out ExportReservation? reservation,
        out string code,
        string? requestFingerprint = null)
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

            PruneExportsLocked(entry, now);

            if (entry.Exports.TryGetValue(exportId, out var previous))
            {
                if (string.Equals(previous.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
                    previous.Size == size && string.Equals(previous.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
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
                RequestFingerprint = requestFingerprint,
                Completion = completion,
                CreatedAt = now,
            };
            reservation = new ExportReservation(entry.Context, completion.Task, true);
            code = "accepted";
            return true;
        }
    }

    public bool TryAuthorizeOperation(
        string pluginInstanceId,
        string contextId,
        string capability,
        out InstantEditImportContext? context,
        out string code)
    {
        context = null;
        code = "invalid_context";
        lock (_lock)
        {
            if (_disposed)
            {
                code = "server_stopped";
                return false;
            }
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
            context = entry.Context;
            code = "accepted";
            return true;
        }
    }

    public void CompleteExport(string contextId, string exportId, ExportReceipt receipt)
    {
        lock (_lock)
        {
            if (_contexts.TryGetValue(contextId, out var context) && context.Exports.TryGetValue(exportId, out var export))
            {
                export.CompletedAt = DateTimeOffset.UtcNow;
                export.Completion.TrySetResult(receipt);
                PruneExportsLocked(context, DateTimeOffset.UtcNow);
            }
        }
    }

    public bool TryGetExportStatus(
        string pluginInstanceId,
        string contextId,
        string exportId,
        string capability,
        out Task<ExportReceipt>? completion,
        out string code)
    {
        completion = null;
        if (!TryAuthorizeOperation(pluginInstanceId, contextId, capability, out _, out code))
            return false;

        lock (_lock)
        {
            if (!_contexts.TryGetValue(contextId, out var entry) || !IsSafeId(exportId))
            {
                code = "export_not_found";
                return false;
            }

            PruneExportsLocked(entry, DateTimeOffset.UtcNow);
            if (!entry.Exports.TryGetValue(exportId, out var export))
            {
                code = "export_not_found";
                return false;
            }

            completion = export.Completion.Task;
            code = completion.IsCompleted ? "export_complete" : "export_pending";
            return true;
        }
    }

    public bool TryRevoke(
        string contextId,
        string importId,
        string capability,
        out string code)
    {
        var removed = false;
        lock (_lock)
        {
            if (_disposed)
            {
                code = "server_stopped";
                return false;
            }
            if (!IsSafeId(contextId) || !_contexts.TryGetValue(contextId, out var entry))
            {
                code = "context_revoked";
                return true;
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

            _contexts.Remove(contextId);
            CompletePendingLocked(entry, "context_removed");
            removed = true;
        }

        if (removed)
            Persist();
        code = "context_revoked";
        return true;
    }

    public void RemoveContext(string contextId)
    {
        var removed = false;
        lock (_lock)
        {
            if (_contexts.Remove(contextId, out var context))
            {
                CompletePendingLocked(context, "context_removed");
                removed = true;
            }
        }
        if (removed)
            Persist();
    }

    private static void PruneExportsLocked(ContextEntry context, DateTimeOffset now)
    {
        foreach (var exportId in context.Exports
                     .Where(pair => pair.Value.CompletedAt is { } completed && now - completed > ReceiptRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
            context.Exports.Remove(exportId);

        var completedExports = context.Exports
            .Where(pair => pair.Value.CompletedAt is not null)
            .OrderByDescending(pair => pair.Value.CompletedAt)
            .Skip(MaxCompletedReceiptsPerContext)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var exportId in completedExports)
            context.Exports.Remove(exportId);
    }

    private static void CompletePendingLocked(ContextEntry context, string code)
    {
        foreach (var export in context.Exports.Values)
            export.Completion.TrySetResult(new ExportReceipt(false, code, "the export context is no longer available"));
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;
        try
        {
            return Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            throw new ArgumentException("The source mod root is invalid.", nameof(root));
        }
    }

    private static string? ResolveTargetRelativePath(
        string targetFilePath,
        string? sourceModRootPath,
        string? suppliedRelativePath)
    {
        var relative = suppliedRelativePath?.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(relative) && sourceModRootPath is not null &&
            IsPathWithin(targetFilePath, sourceModRootPath))
            relative = Path.GetRelativePath(sourceModRootPath, targetFilePath).Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(relative))
            return null;
        if (!PenumbraService.IsSafeRelativeModelPath(relative))
            throw new ArgumentException("The target-relative model path is invalid.", nameof(suppliedRelativePath));

        if (sourceModRootPath is not null)
        {
            var combined = Path.GetFullPath(Path.Combine(
                sourceModRootPath,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(combined, sourceModRootPath) ||
                !string.Equals(combined, targetFilePath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The target-relative path does not match the original model.", nameof(suppliedRelativePath));
        }

        return relative;
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

    private InstantEditImportContext RuntimeContext(PersistedExportContext saved)
    {
        var safeManifest = IsSafeResourceManifest(saved.ResourceManifest)
            ? saved.ResourceManifest
            : null;
        return new InstantEditImportContext
        {
            PluginInstanceId = PluginInstanceId,
            ContextId = saved.ContextId,
            ImportId = saved.ImportId,
            Capability = saved.Capability,
            GamePath = saved.GamePath,
            SourceKind = saved.Version >= 2 ? saved.SourceKind! : InstantEditImportContext.ModSource,
            ResolvedGamePath = saved.Version >= 2 ? saved.ResolvedGamePath! : saved.GamePath,
            DestinationState = saved.Version >= 2 ? saved.DestinationState! : InstantEditImportContext.ReadyDestination,
            ObjectIndex = saved.ObjectIndex,
            TargetFilePath = saved.TargetFilePath,
            TargetFolder = saved.TargetFolder,
            SourceModDirectory = saved.SourceModDirectory,
            SourceModName = saved.SourceModName,
            SourceModRootPath = saved.SourceModRootPath,
            TargetRelativePath = saved.TargetRelativePath,
            TargetCollectionId = saved.TargetCollectionId,
            TargetCollectionName = saved.TargetCollectionName,
            CallbackPort = saved.CallbackPort,
            ResourceManifest = safeManifest,
            ResourceManifestStatus = safeManifest is not null ? "ready" : "capture_failed",
        };
    }

    private static bool IsSafePersistedContext(PersistedExportContext saved)
    {
        if (!IsSafeId(saved.ContextId) || !IsSafeId(saved.ImportId) ||
            !CapabilityMatches(saved.Capability, saved.Capability) ||
            !PenumbraService.IsSafeGamePath(saved.GamePath) ||
            saved.CallbackPort is < 1 or > 65535)
            return false;
        if (saved.Version < 2)
            return PenumbraService.IsSafeModName(saved.SourceModDirectory) &&
                   PenumbraService.IsSafeLocalModelPath(saved.TargetFilePath) &&
                   PenumbraService.IsSafeRelativeModelPath(saved.TargetRelativePath) &&
                   !string.IsNullOrWhiteSpace(saved.SourceModName) && !string.IsNullOrWhiteSpace(saved.TargetFolder);
        if (saved.SourceKind is not (InstantEditImportContext.ModSource or InstantEditImportContext.GameSource) ||
            !PenumbraService.IsSafeGamePath(saved.ResolvedGamePath) ||
            saved.DestinationState is not (InstantEditImportContext.ReadyDestination or InstantEditImportContext.NewModRequiredDestination) ||
            saved.TargetCollectionId == Guid.Empty ||
            (saved.TargetCollectionName is not null && saved.TargetCollectionName.Length > 512))
            return false;
        if (saved.DestinationState == InstantEditImportContext.NewModRequiredDestination)
            return saved.SourceKind == InstantEditImportContext.GameSource && saved.TargetFilePath is null &&
                   saved.TargetFolder is null && saved.SourceModDirectory is null && saved.SourceModName is null &&
                   saved.SourceModRootPath is null && saved.TargetRelativePath is null;
        return PenumbraService.IsSafeModName(saved.SourceModDirectory) &&
               PenumbraService.IsSafeLocalModelPath(saved.TargetFilePath) &&
               PenumbraService.IsSafeRelativeModelPath(saved.TargetRelativePath) &&
               !string.IsNullOrWhiteSpace(saved.SourceModName) && !string.IsNullOrWhiteSpace(saved.TargetFolder);
    }

    private static bool IsSafeResourceManifest(ResourceDependencyManifest? manifest)
    {
        if (manifest is null)
            return true;
        if (manifest.Version != ResourceDependencyManifest.CurrentVersion ||
            manifest.Materials.Count is < 1 or > 256)
            return false;

        foreach (var material in manifest.Materials)
        {
            if (string.IsNullOrWhiteSpace(material.ModelMaterial) || material.ModelMaterial.Length > 512 ||
                !PenumbraService.IsSafeGameResourcePath(material.GamePath, ".mtrl") || material.Textures.Count > 1024 ||
                !IsSafeResourceLocator(material.Resource))
                return false;
            foreach (var texture in material.Textures)
                if (!PenumbraService.IsSafeGameResourcePath(texture.StoredGamePath, ".tex") ||
                    !PenumbraService.IsSafeGameResourcePath(texture.EffectiveGamePath, ".tex") ||
                    !IsSafeResourceLocator(texture.Resource))
                    return false;
        }
        return true;
    }

    private static bool IsSafeResourceLocator(SourceResourceLocator locator)
    {
        if (locator.Sha256.Length != 64 || locator.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
            !PenumbraService.IsSafeGameResourcePath(locator.GamePath, ".mtrl", ".tex"))
            return false;
        if (locator.Kind == "game")
            return locator.SourceModDirectory is null && locator.SourceRelativePath is null;
        return locator.Kind == "mod" && PenumbraService.IsSafeModName(locator.SourceModDirectory) &&
               PenumbraService.IsSafeRelativeResourcePath(locator.SourceRelativePath);
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
                    .Select(entry => PersistedExportContext.FromContext(entry.Context))
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
            if (_disposed)
                return;
            _disposed = true;
            foreach (var context in _contexts.Values)
                CompletePendingLocked(context, "server_stopped");
            _contexts.Clear();
        }
    }
}
