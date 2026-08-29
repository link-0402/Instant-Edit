using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Lumina.Data;
using Lumina.Data.Files;

namespace InstantEdit.Services;

/// <summary> Result of applying an export to Penumbra. </summary>
public sealed record ExportResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyList<string>? Warnings = null,
    string? TargetFilePath = null,
    string? DestinationName = null,
    ModPathRemap? PathRemap = null)
{
    public ExportResult(bool success, string message)
        : this(success, success ? "export_applied" : "apply_failed", message)
    {
    }

    public IReadOnlyList<string> WarningList => Warnings ?? Array.Empty<string>();
}

/// <summary>Physical paths changed by a successful in-place mod cleanup.</summary>
public sealed record ModPathRemap(
    string ModDirectory,
    string ModRoot,
    IReadOnlyDictionary<string, string> RelativePaths);

public sealed record VariantOptionTarget(string Id, string Name, string ModelPath);
public sealed record VariantGroupTarget(string Id, string Name, IReadOnlyList<VariantOptionTarget> Options);
public sealed record VariantTargetsResult(bool Success, string Code, string Message, IReadOnlyList<VariantGroupTarget> Groups);
public sealed record MashupContributor(InstantEditImportContext Context, IReadOnlyList<string> Materials);
public sealed record MashupMaterialAssignment(
    string ContextId,
    string ModelMaterial,
    string Alias,
    string GamePath,
    string? Slot);
public sealed record MashupPlanResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyList<MashupMaterialAssignment> Assignments,
    string? Fingerprint = null);

/// <summary>
/// Wraps all Penumbra IPC used by XIV Instant Edit:
/// reading the resolved model files of on-screen game objects and
/// writing/updating a persistent mod so the edited model applies in-game.
/// </summary>
public sealed class PenumbraService
{
    internal sealed record SourceModTarget(string Directory, string Folder, string FilePath, string RelativePath);
    internal sealed record SourceTargetResolution(SourceModTarget? Target, string Code, string? Error);

    private const string OwnershipMarkerFile = ".instant-edit-owner.json";
    private const string VariantGroupDescriptionPrefix = "Managed by XIV Instant Edit variant group: ";

    private readonly IDalamudPluginInterface  _pi;
    private readonly GetGameObjectResourcePaths _getPaths;
    private readonly GetGameObjectResourceTrees _getObjectTrees;
    private readonly GetPlayerResourceTrees _getPlayerTrees;
    private readonly GetModDirectory           _getModDirectory;
    private readonly GetModList                 _getModList;
    private readonly GetModPath                 _getModPath;
    private readonly AddMod                     _addMod;
    private readonly ReloadMod                  _reloadMod;
    private readonly GetCollectionForObject      _getCollectionForObject;
    private readonly TrySetMod                   _trySetMod;
    private readonly TrySetModPriority            _trySetModPriority;
    private readonly RedrawObject               _redrawObject;
    private readonly IFramework                  _framework;
    private readonly IPluginLog                 _log;
    private readonly IObjectTable?              _objects;
    private readonly IDataManager?              _data;
    private readonly SemaphoreSlim              _exportGate = new(1, 1);

    public PenumbraService(
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log,
        IObjectTable? objects = null,
        IDataManager? data = null)
    {
        _pi               = pi;
        _log             = log;
        _framework       = framework;
        _objects         = objects;
        _data            = data;
        _getPaths        = new GetGameObjectResourcePaths(pi);
        _getObjectTrees  = new GetGameObjectResourceTrees(pi);
        _getPlayerTrees  = new GetPlayerResourceTrees(pi);
        _getModDirectory = new GetModDirectory(pi);
        _getModList      = new GetModList(pi);
        _getModPath      = new GetModPath(pi);
        _addMod          = new AddMod(pi);
        _reloadMod       = new ReloadMod(pi);
        _getCollectionForObject = new GetCollectionForObject(pi);
        _trySetMod       = new TrySetMod(pi);
        _trySetModPriority = new TrySetModPriority(pi);
        _redrawObject    = new RedrawObject(pi);
    }

    /// <summary> Whether Penumbra is loaded and the resource tree IPC is available. </summary>
    public bool Available
    {
        get
        {
            try
            {
                return _getPlayerTrees.Valid;
            }
            catch (Exception e)
            {
                _log.Debug($"Could not query Penumbra availability: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Get the resolved resource paths for one game object.
    /// The dictionary maps the resolved local path (mod file on disk, or the game path if unmodded)
    /// to the set of game paths it serves.
    /// </summary>
    public Dictionary<string, HashSet<string>>? GetResourcePaths(ushort gameObjectIndex)
    {
        try
        {
            if (!Available)
                return null;

            var result = _getPaths.Invoke(gameObjectIndex);
            return result is { Length: > 0 } ? result[0] : null;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra resource paths: {e.Message}");
            return null;
        }
    }

    /// <summary>Resolve one object's effective resources on Dalamud's framework thread.</summary>
    public async Task<Dictionary<string, HashSet<string>>?> GetResourcePathsAsync(ushort gameObjectIndex)
        => await _framework.RunOnFrameworkThread(() => GetResourcePaths(gameObjectIndex)).ConfigureAwait(false);

    /// <summary> Get the resolved resource paths for several game objects in one IPC call. </summary>
    public Dictionary<string, HashSet<string>>?[] GetResourcePaths(ushort[] gameObjectIndices)
    {
        if (gameObjectIndices.Length == 0)
            return Array.Empty<Dictionary<string, HashSet<string>>?>();

        try
        {
            if (!Available)
                return EmptyResourceResults(gameObjectIndices.Length);

            var result = _getPaths.Invoke(gameObjectIndices);
            if (result is null || result.Length != gameObjectIndices.Length)
                return EmptyResourceResults(gameObjectIndices.Length);
            return result;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra resource paths: {e.Message}");
            return EmptyResourceResults(gameObjectIndices.Length);
        }
    }

    /// <summary>
    /// Gets Penumbra's local-player-owned resource trees, including the public UI
    /// labels and icons. The returned index is the current object-table index.
    /// </summary>
    public IReadOnlyDictionary<ushort, ResourceTreeDto> GetPlayerResourceTrees()
    {
        try
        {
            if (!Available)
                return new Dictionary<ushort, ResourceTreeDto>();

            return _getPlayerTrees.Invoke(withUiData: true);
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra player resource trees: {e.Message}");
            return new Dictionary<ushort, ResourceTreeDto>();
        }
    }

    public string? GetModDirectory()
    {
        try
        {
            var dir = _getModDirectory.Invoke();
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra mod directory: {e.Message}");
            return null;
        }
    }

    public Dictionary<string, string> GetModList()
    {
        try
        {
            return _getModList.Invoke() ?? new Dictionary<string, string>();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra mod list: {e.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Write an export back to the resolved file in its original Penumbra mod.
    /// The destination is authoritative while the server-owned import context
    /// is alive; only the source mod registration and destination folder are
    /// checked again before writing.
    /// </summary>
    public async Task<ExportResult> ApplySourceExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        string exportedFile,
        string? variantName,
        string? variantGroupName,
        string? variantTarget,
        string? variantTargetId,
        bool setupVariantInPenumbra,
        bool backupExisting = false)
    {
        if (!IsSafeModName(sourceModDirectory) || !IsSafeGamePath(sourceGamePath) ||
            !IsSafeLocalModelPath(sourceFilePath))
            return new ExportResult(false, "destination_unsafe", "The original Penumbra model destination is invalid.");
        var validationError = ValidateExportRequest(sourceModDirectory, sourceGamePath, exportedFile);
        if (validationError is not null)
            return new ExportResult(false, "invalid_export_file", validationError);
        if (variantName is not null && !IsSafeVariantName(variantName))
            return new ExportResult(false, "invalid_variant_name", "Invalid variant name.");
        if (variantName is not null && string.Equals(
                variantName,
                Path.GetFileNameWithoutExtension(sourceGamePath),
                StringComparison.OrdinalIgnoreCase))
            return new ExportResult(false, "invalid_variant_name", "Variant name must differ from the originally imported model name.");
        if (setupVariantInPenumbra && variantName is null && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            return new ExportResult(false, "invalid_variant", "Penumbra variant setup requires Save as Variant.");
        if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal) &&
            !IsSafeVariantGroupName(variantGroupName))
            return new ExportResult(false, "invalid_variant_group", "Penumbra variant setup requires an option group name.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        string? committedTarget = null;
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => ResolveSourceModTargetOnFramework(
                    sourceModDirectory,
                    sourceFilePath,
                    sourceModRootPath,
                    targetRelativePath)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new ExportResult(
                    false,
                    resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.");

            var targetFile = variantName is null
                ? resolved.Target.FilePath
                : Path.Combine(Path.GetDirectoryName(resolved.Target.FilePath)!, variantName + ".mdl");
            if (setupVariantInPenumbra && string.Equals(variantTarget, "option", StringComparison.Ordinal))
            {
                var optionTarget = ResolveVariantOptionTarget(
                    resolved.Target.Folder, sourceGamePath, variantTargetId);
                if (optionTarget.Error is not null)
                    return new ExportResult(false, optionTarget.Code, optionTarget.Error);
                targetFile = optionTarget.FilePath!;
            }
            if (setupVariantInPenumbra && string.Equals(variantTarget, "group", StringComparison.Ordinal))
            {
                var groupError = ValidateVariantGroupTarget(resolved.Target.Folder, sourceGamePath, variantTargetId);
                if (groupError is not null)
                    return new ExportResult(false, "stale_variant_target", groupError);
            }
            var writeError = WriteModelToOriginalLocation(
                resolved.Target.Folder,
                targetFile,
                exportedFile,
                backupExisting);
            if (writeError is not null)
                return new ExportResult(false, "write_failed", writeError);
            committedTarget = targetFile;

            var warnings = new List<string>();

            if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            {
                var relativeVariantPath = Path.GetRelativePath(resolved.Target.Folder, targetFile).Replace('\\', '/');
                var groupError = WriteVariantGroup(
                    resolved.Target.Folder,
                    sourceGamePath,
                    relativeVariantPath,
                    variantName!,
                    variantGroupName!);
                if (groupError is not null)
                    warnings.Add($"Penumbra variant setup failed: {groupError}");
            }

            var reloadError = await _framework.RunOnFrameworkThread(
                () => ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                warnings.Add(reloadError.Message);
            else
            {
                var redrawWarning = await _framework.RunOnFrameworkThread(
                    RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                if (redrawWarning is not null)
                    warnings.Add(redrawWarning);
            }

            var code = warnings.Count == 0 ? "export_applied" : "export_applied_with_warnings";
            var message = warnings.Count == 0
                ? $"Exported to {targetFile} and reloaded {resolved.Target.Directory}."
                : $"Exported to {targetFile}; {warnings.Count} follow-up warning(s).";
            return new ExportResult(true, code, message, warnings, targetFile);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to export to the original Penumbra mod.");
            if (committedTarget is not null)
                return new ExportResult(
                    true,
                    "export_applied_with_warnings",
                    $"Exported to {committedTarget}; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, "write_failed", $"Failed before the model write could be committed: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>Read Single groups whose options replace this context's model path.</summary>
    public async Task<VariantTargetsResult> GetVariantTargetsAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath)
    {
        if (!IsSafeModName(sourceModDirectory) || !IsSafeLocalModelPath(sourceFilePath) || !IsSafeGamePath(sourceGamePath))
            return new VariantTargetsResult(false, "destination_unsafe", "The original Penumbra model destination is invalid.", []);

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => ResolveSourceModTargetOnFramework(
                    sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new VariantTargetsResult(false, resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.", []);
            return new VariantTargetsResult(true, "variant_targets_loaded", "Compatible Penumbra targets loaded.",
                ReadVariantTargets(resolved.Target.Folder, sourceGamePath));
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to read Penumbra variant targets.");
            return new VariantTargetsResult(false, "variant_targets_unavailable", "Could not read Penumbra option groups.", []);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>Get resource trees for explicit object-table indices.</summary>
    public ResourceTreeDto?[] GetResourceTrees(ushort[] gameObjectIndices)
    {
        if (gameObjectIndices.Length == 0)
            return Array.Empty<ResourceTreeDto?>();

        try
        {
            if (!Available)
                return Array.Empty<ResourceTreeDto?>();

            return _getObjectTrees.Invoke(withUiData: true, gameObjectIndices) ?? Array.Empty<ResourceTreeDto?>();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve explicit resource trees: {e.Message}");
            return Array.Empty<ResourceTreeDto?>();
        }
    }

    public async Task<ExportResult> ApplyMashupAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        MashupPlanResult plan,
        string exportedFile,
        string exportId,
        string destination,
        string name)
    {
        if (_data is null)
            return new ExportResult(false, "mashup_unavailable", "Game data access is unavailable.");
        if (!plan.Success || string.IsNullOrWhiteSpace(plan.Fingerprint) ||
            !IsSafeVariantGroupName(name) || exportId.Length is < 8 or > 128 ||
            destination is not ("active_mod" or "new_mod") || contributors.Count is < 2 or > 16)
            return new ExportResult(false, "invalid_mashup", "The mashup request is invalid.");
        if (contributors.All(item => item.Context.ContextId != activeContext.ContextId) ||
            contributors.Select(item => item.Context.SourceModDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
            return new ExportResult(false, "invalid_mashup", "The active Context and at least two source mods must contribute.");
        if (contributors.Any(item => item.Context.ResourceManifest?.Version != ResourceDependencyManifest.CurrentVersion))
            return new ExportResult(false, "mashup_reimport_required", "Re-import every contributing Context before creating a mashup.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var activeTarget = await _framework.RunOnFrameworkThread(() => ResolveSourceModTargetOnFramework(
                activeContext.SourceModDirectory,
                activeContext.TargetFilePath,
                activeContext.SourceModRootPath,
                activeContext.TargetRelativePath)).ConfigureAwait(false);
            if (activeTarget.Target is null)
                return new ExportResult(false, activeTarget.Code,
                    activeTarget.Error ?? "The active Penumbra mod is no longer available.");

            var modelBytes = await File.ReadAllBytesAsync(exportedFile).ConfigureAwait(false);
            var prepared = await PrepareMashupAsync(
                activeContext, contributors, plan, modelBytes, exportId).ConfigureAwait(false);
            if (prepared.Error is not null)
                return new ExportResult(false, prepared.Code, prepared.Error);

            var description = FormatMashupDescription(contributors);

            return destination == "active_mod"
                ? await CommitMashupToActiveModAsync(activeTarget.Target, activeContext, prepared, exportId, name, description)
                    .ConfigureAwait(false)
                : await CommitMashupToNewModAsync(activeContext, prepared, exportId, name, description)
                    .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to create Penumbra mashup.");
            return new ExportResult(false, "mashup_failed", e.Message);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private sealed record PreparedMashup(
        byte[] ModelBytes,
        Dictionary<string, byte[]> Files,
        Dictionary<string, string> Mappings,
        string Code = "accepted",
        string? Error = null);

    private async Task<PreparedMashup> PrepareMashupAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        MashupPlanResult plan,
        byte[] modelBytes,
        string exportId)
    {
        var expectedAliases = plan.Assignments
            .Select(item => NormalizeModelMaterial(item.Alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var materialEntries = new List<(
            MashupContributor Contributor,
            MaterialDependency Material,
            MashupMaterialAssignment Assignment)>();

        foreach (var assignment in plan.Assignments)
        {
            var contributor = contributors.FirstOrDefault(item =>
                string.Equals(item.Context.ContextId, assignment.ContextId, StringComparison.Ordinal));
            if (contributor is null)
                return new PreparedMashup(modelBytes, files, mappings, "mashup_plan_mismatch",
                    "The material plan references an unknown Context.");
            var manifest = contributor.Context.ResourceManifest!;
            var dependency = manifest.Materials.FirstOrDefault(material =>
                string.Equals(NormalizeModelMaterial(material.ModelMaterial), assignment.ModelMaterial,
                    StringComparison.OrdinalIgnoreCase));
            if (dependency is null)
                return new PreparedMashup(modelBytes, files, mappings, "mashup_material_missing",
                    $"Material {assignment.ModelMaterial} was not captured for {contributor.Context.SourceModName}; re-import that Context.");
            materialEntries.Add((contributor, dependency, assignment));
        }

        var actualAliases = MaterialPreviewBundleBuilder.ReadModelMaterials(modelBytes)
            .Select(NormalizeModelMaterial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualAliases.SetEquals(expectedAliases))
            return new PreparedMashup(modelBytes, files, mappings, "mashup_model_mismatch",
                "The exported MDL material aliases do not match the authenticated contributor list.");

        var texturePhysicalByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textureHashByGamePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in materialEntries)
        {
            var materialBytes = await ReadManifestResourceAsync(entry.Material.Resource).ConfigureAwait(false);
            if (materialBytes is null)
                return new PreparedMashup(modelBytes, files, mappings, "mashup_source_changed",
                    $"Material source changed or disappeared: {entry.Material.GamePath}. Re-import the Context.");

            var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usages = MaterialPreviewBundleBuilder.ReadTextureUsages(materialBytes);
            var indexedTextures = entry.Material.Textures
                .Select((texture, index) => (Texture: texture, Index: index))
                .GroupBy(item => NormalizeGamePath(item.Texture.StoredGamePath), StringComparer.OrdinalIgnoreCase);
            foreach (var storedGroup in indexedTextures)
            {
                var captured = new List<(TextureDependency Texture, int Index, string Hash, string Relative)>();
                foreach (var item in storedGroup)
                {
                    var textureBytes = await ReadManifestResourceAsync(item.Texture.Resource).ConfigureAwait(false);
                    if (textureBytes is null)
                        return new PreparedMashup(modelBytes, files, mappings, "mashup_source_changed",
                            $"Texture source changed or disappeared: {item.Texture.EffectiveGamePath}. Re-import the Context.");
                    var hash = item.Texture.Resource.Sha256.ToLowerInvariant();
                    if (!texturePhysicalByHash.TryGetValue(hash, out var relativeTexture))
                    {
                        relativeTexture = $"Files/xiv-instant-edit/mashups/{exportId[..12]}/textures/{hash[..24]}.tex";
                        texturePhysicalByHash[hash] = relativeTexture;
                        files[relativeTexture] = textureBytes;
                    }
                    captured.Add((item.Texture, item.Index, hash, relativeTexture));
                }

                string storedAlias;
                try
                {
                    var usage = captured
                        .Select(item => item.Index < usages.Count ? usages[item.Index] : "other")
                        .FirstOrDefault(item => item != "other") ?? "other";
                    storedAlias = PlanMashupTexturePath(
                        activeContext.GamePath,
                        entry.Contributor.Context.GamePath,
                        storedGroup.Key,
                        entry.Assignment.Slot,
                        usage,
                        captured[0].Index,
                        captured.Select(item => (item.Texture.Flags, item.Hash)).ToArray(),
                        textureHashByGamePath,
                        mappings);
                    rewrites[storedGroup.Key] = storedAlias;
                }
                catch (Exception e)
                {
                    return new PreparedMashup(modelBytes, files, mappings, "mashup_texture_conflict",
                        $"Could not retarget texture {storedGroup.Key}: {e.Message}");
                }

                foreach (var item in captured)
                {
                    var effective = Dx11TexturePath(storedAlias, item.Texture.Flags);
                    if (textureHashByGamePath.TryGetValue(effective, out var existingHash) &&
                        !string.Equals(existingHash, item.Hash, StringComparison.OrdinalIgnoreCase))
                        return new PreparedMashup(modelBytes, files, mappings, "mashup_texture_conflict",
                            $"Generated texture path still conflicts: {effective}.");
                    textureHashByGamePath[effective] = item.Hash;
                    mappings[effective] = item.Relative;
                }
            }

            byte[] rewritten;
            try
            {
                rewritten = RewriteMaterialTexturePaths(materialBytes, rewrites);
            }
            catch (Exception e)
            {
                return new PreparedMashup(modelBytes, files, mappings, "mashup_material_rewrite_failed",
                    $"Could not rewrite {entry.Material.GamePath}: {e.Message}");
            }
            var materialFileName = Path.GetFileName(entry.Assignment.Alias);
            var relativeMaterial = $"Files/xiv-instant-edit/mashups/{exportId[..12]}/materials/{materialFileName}";
            files[relativeMaterial] = rewritten;
            mappings[entry.Assignment.GamePath] = relativeMaterial;
        }

        var relativeModel = $"Files/xiv-instant-edit/mashups/{exportId[..12]}/model.mdl";
        files[relativeModel] = modelBytes;
        mappings[NormalizeGamePath(activeContext.GamePath)] = relativeModel;
        return new PreparedMashup(modelBytes, files, mappings);
    }

    private async Task<byte[]?> ReadManifestResourceAsync(SourceResourceLocator locator)
    {
        byte[]? bytes = null;
        if (locator.Kind == "game")
        {
            try
            {
                bytes = (await _data!.GetFileAsync<FileResource>(locator.GamePath, CancellationToken.None)
                    .ConfigureAwait(false))?.Data;
            }
            catch (Exception e)
            {
                _log.Debug(e, "Could not read mashup game resource {GamePath}.", locator.GamePath);
            }
        }
        else if (locator.Kind == "mod" && IsSafeModName(locator.SourceModDirectory) &&
                 IsSafeRelativeResourcePath(locator.SourceRelativePath))
        {
            var roots = await _framework.RunOnFrameworkThread(
                () => GetRegisteredManifestRoots(locator.SourceModDirectory!)).ConfigureAwait(false);
            if (roots.Length == 0)
                return null;
            return await ReadVerifiedModManifestResourceAsync(locator, roots).ConfigureAwait(false);
        }

        if (bytes is not { Length: > 0 })
            return null;
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        return string.Equals(actualHash, locator.Sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    private string[] GetRegisteredManifestRoots(string modDirectory)
    {
        if (!GetMods().Any(mod =>
                string.Equals(mod.Directory, modDirectory, StringComparison.OrdinalIgnoreCase)))
            return [];

        var roots = new List<string>();
        AddCandidateRoot(roots, GetRegisteredModPath(modDirectory));
        var configuredRoot = GetModDirectory();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            AddCandidateRoot(roots, Path.Combine(configuredRoot, modDirectory));
        return roots.ToArray();
    }

    internal static async Task<byte[]?> ReadVerifiedModManifestResourceAsync(
        SourceResourceLocator locator,
        IEnumerable<string> authorizedRoots)
    {
        if (locator.Kind != "mod" || !IsSafeModName(locator.SourceModDirectory) ||
            !IsSafeRelativeResourcePath(locator.SourceRelativePath) ||
            locator.Sha256.Length != 64 || locator.Sha256.Any(character => !Uri.IsHexDigit(character)))
            return null;

        foreach (var candidate in authorizedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = NormalizePhysicalPath(candidate);
            if (root is null || !Directory.Exists(root) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                continue;

            var file = Path.GetFullPath(Path.Combine(
                root, locator.SourceRelativePath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(file, root) || !File.Exists(file) ||
                HasReparsePointInPath(root, file) ||
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                continue;

            var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.Equals(actualHash, locator.Sha256, StringComparison.OrdinalIgnoreCase))
                return bytes;
        }

        return null;
    }

    internal static byte[] RewriteMaterialTexturePaths(
        byte[] materialBytes,
        IReadOnlyDictionary<string, string> rewrites)
    {
        if (rewrites.Count == 0)
            return materialBytes.ToArray();
        var mtrl = MaterialPreviewBundleBuilder.LooseLuminaFile.Load<MtrlFile>(materialBytes);
        var stringTableStart = checked(16 + 4 * (
            mtrl.FileHeader.TextureCount + mtrl.FileHeader.UvSetCount + mtrl.FileHeader.ColorSetCount));
        var oldStringTableEnd = checked(stringTableStart + mtrl.FileHeader.StringTableSize);
        if (oldStringTableEnd > materialBytes.Length)
            throw new InvalidDataException("the MTRL string table is truncated");

        var offsetMap = new Dictionary<int, int>();
        using var stringStream = new MemoryStream();
        for (var cursor = 0; cursor < mtrl.Strings.Length;)
        {
            var end = Array.IndexOf(mtrl.Strings, (byte)0, cursor);
            if (end < 0)
                end = mtrl.Strings.Length;
            var oldValue = Encoding.UTF8.GetString(mtrl.Strings, cursor, end - cursor);
            var value = rewrites.TryGetValue(NormalizeGamePath(oldValue), out var replacement)
                ? replacement
                : oldValue;
            offsetMap[cursor] = checked((int)stringStream.Position);
            stringStream.Write(Encoding.UTF8.GetBytes(value));
            if (end < mtrl.Strings.Length)
                stringStream.WriteByte(0);
            cursor = end < mtrl.Strings.Length ? end + 1 : end;
        }

        var newStrings = stringStream.ToArray();
        if (newStrings.Length > ushort.MaxValue)
            throw new InvalidDataException("rewritten MTRL string table is too large");
        var newLength = checked(materialBytes.Length - mtrl.Strings.Length + newStrings.Length);
        if (newLength > ushort.MaxValue)
            throw new InvalidDataException("rewritten MTRL is too large");
        var rewritten = new byte[newLength];
        materialBytes.AsSpan(0, stringTableStart).CopyTo(rewritten);
        newStrings.CopyTo(rewritten, stringTableStart);
        materialBytes.AsSpan(oldStringTableEnd).CopyTo(rewritten.AsSpan(stringTableStart + newStrings.Length));

        static void RewriteOffset(byte[] bytes, int position, IReadOnlyDictionary<int, int> offsets)
        {
            var oldOffset = BitConverter.ToUInt16(bytes, position);
            if (!offsets.TryGetValue(oldOffset, out var newOffset) || newOffset > ushort.MaxValue)
                throw new InvalidDataException("an MTRL string offset is invalid");
            BitConverter.TryWriteBytes(bytes.AsSpan(position, sizeof(ushort)), (ushort)newOffset);
        }

        BitConverter.TryWriteBytes(rewritten.AsSpan(4, sizeof(ushort)), (ushort)newLength);
        BitConverter.TryWriteBytes(rewritten.AsSpan(8, sizeof(ushort)), (ushort)newStrings.Length);
        RewriteOffset(rewritten, 10, offsetMap);
        var entryCount = mtrl.FileHeader.TextureCount + mtrl.FileHeader.UvSetCount + mtrl.FileHeader.ColorSetCount;
        for (var index = 0; index < entryCount; ++index)
            RewriteOffset(rewritten, 16 + index * 4, offsetMap);

        var validated = MaterialPreviewBundleBuilder.LooseLuminaFile.Load<MtrlFile>(rewritten);
        foreach (var replacement in rewrites.Values)
            if (!validated.TextureOffsets.Any(offset =>
                    string.Equals(ReadNullTerminated(validated.Strings, offset.Offset), replacement,
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("rewritten MTRL did not retain every texture alias");
        return rewritten;
    }

    private async Task<ExportResult> CommitMashupToActiveModAsync(
        SourceModTarget target,
        InstantEditImportContext activeContext,
        PreparedMashup prepared,
        string exportId,
        string requestedName,
        string description)
    {
        var namespaceRelative = $"Files/xiv-instant-edit/mashups/{exportId[..12]}";
        var namespaceFolder = Path.Combine(target.Folder, namespaceRelative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(namespaceFolder))
            return new ExportResult(false, "mashup_destination_exists", "The mashup namespace already exists.");
        var committed = false;
        var actualName = requestedName;
        try
        {
            foreach (var file in prepared.Files)
                WriteBytesAtomic(target.Folder, file.Key, file.Value);
            actualName = UniqueMashupGroupName(target.Folder, requestedName);
            var groupError = WriteMashupGroup(target.Folder, actualName, prepared.Mappings, description);
            if (groupError is not null)
                throw new InvalidDataException(groupError);
            committed = true;

            var warnings = new List<string>();
            var cleanup = NormalizeAndDeduplicateMod(target.Folder, target.Directory);
            warnings.AddRange(cleanup.Warnings);
            try
            {
                var reloadError = await _framework.RunOnFrameworkThread(
                    () => ReloadModOnFramework(target.Directory)).ConfigureAwait(false);
                if (reloadError is not null)
                    warnings.Add(reloadError.Message);
                else
                {
                    var redraw = await _framework.RunOnFrameworkThread(
                        RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                    if (redraw is not null)
                        warnings.Add(redraw);
                }
            }
            catch (Exception e)
            {
                warnings.Add($"The mashup was committed, but Penumbra refresh failed: {e.Message}");
            }
            var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            if (cleanup.PathRemap?.RelativePaths.TryGetValue(modelRelative, out var remappedModel) == true)
                modelRelative = remappedModel;
            var modelPath = Path.Combine(target.Folder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
            return new ExportResult(true,
                warnings.Count == 0 ? "mashup_applied" : "mashup_applied_with_warnings",
                $"Created mashup group {actualName} in {target.Directory}.", warnings, modelPath, actualName,
                cleanup.PathRemap);
        }
        catch (Exception e)
        {
            if (!committed)
            {
                TryDeleteMashupNamespace(target.Folder, namespaceFolder);
                return new ExportResult(false, "mashup_write_failed", e.Message);
            }
            var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            var modelPath = Path.Combine(target.Folder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
            return new ExportResult(true, "mashup_applied_with_warnings",
                $"Created mashup group {actualName} in {target.Directory}.",
                [$"The mashup was committed, but Penumbra refresh failed: {e.Message}"], modelPath, actualName);
        }
    }

    private async Task<ExportResult> CommitMashupToNewModAsync(
        InstantEditImportContext activeContext,
        PreparedMashup prepared,
        string exportId,
        string modName,
        string description)
    {
        if (!IsSafeNewModName(modName))
            return new ExportResult(false, "invalid_mod_name", "The Penumbra mod name is invalid.");
        var root = await _framework.RunOnFrameworkThread(GetModDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new ExportResult(false, "penumbra_root_missing", "The Penumbra mod root is unavailable.");
        var modList = await _framework.RunOnFrameworkThread(() =>
            TryGetModList(out var mods) ? mods : null).ConfigureAwait(false);
        if (modList is null)
            return new ExportResult(false, "penumbra_unavailable", "Could not retrieve the Penumbra mod list.");
        var conflicts = modList.Keys.Any(key =>
            string.Equals(key, modName, StringComparison.OrdinalIgnoreCase));
        var finalFolder = Path.Combine(root, modName);
        if (conflicts || Directory.Exists(finalFolder) || File.Exists(finalFolder))
            return new ExportResult(false, "mashup_mod_exists", "A Penumbra mod or folder with this name already exists.");

        var staging = Path.Combine(root, $".instant-edit-mashup-{Guid.NewGuid():N}.tmp");
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            WriteJsonAtomic(Path.Combine(staging, "meta.json"), new JsonObject
            {
                ["FileVersion"] = 3,
                ["Name"] = modName,
                ["Author"] = "XIV Instant Edit",
                ["Description"] = description,
                ["Image"] = "",
                ["Version"] = "",
                ["Website"] = "",
                ["ModTags"] = new JsonArray(),
            });
            foreach (var file in prepared.Files)
                WriteBytesAtomic(staging, file.Key, file.Value);
            WriteJsonAtomic(Path.Combine(staging, "default_mod.json"), new JsonObject
            {
                ["Version"] = 0,
                ["Files"] = new JsonObject(prepared.Mappings.Select(pair =>
                    KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            });
            ValidateStagedMashupMod(staging, modName, prepared.Mappings);
            Directory.Move(staging, finalFolder);
            committed = true;

            var warnings = new List<string>();
            var cleanup = NormalizeAndDeduplicateMod(finalFolder, modName);
            warnings.AddRange(cleanup.Warnings);
            try
            {
                var addError = await AddNewModAsync(modName).ConfigureAwait(false);
                if (addError is not null)
                    warnings.Add(addError.Message);
                else
                {
                    var configure = await _framework.RunOnFrameworkThread(
                        () => ConfigureModOnFramework(
                            modName,
                            activeContext.ObjectIndex,
                            setPriority: true,
                            priority: 0)).ConfigureAwait(false);
                    if (!configure.Success)
                        warnings.Add(configure.Message);
                    else
                        warnings.AddRange(configure.WarningList);
                }
            }
            catch (Exception e)
            {
                warnings.Add($"The mashup mod was committed, but Penumbra setup failed: {e.Message}");
            }
            var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            if (cleanup.PathRemap?.RelativePaths.TryGetValue(modelRelative, out var remappedModel) == true)
                modelRelative = remappedModel;
            var modelPath = Path.Combine(finalFolder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
            return new ExportResult(true,
                warnings.Count == 0 ? "mashup_mod_created" : "mashup_mod_created_with_warnings",
                $"Created Penumbra mashup mod {modName}.", warnings, modelPath, modName);
        }
        catch (Exception e)
        {
            if (Directory.Exists(staging))
                TryDeleteMashupNamespace(root, staging);
            if (committed)
            {
                var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
                var modelPath = Path.Combine(finalFolder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
                return new ExportResult(true, "mashup_mod_created_with_warnings",
                    $"Created Penumbra mashup mod {modName}.",
                    [$"The mashup mod was committed, but Penumbra setup failed: {e.Message}"], modelPath, modName);
            }
            return new ExportResult(false, "mashup_mod_create_failed", e.Message);
        }
    }

    private SourceTargetResolution ResolveSourceModTargetOnFramework(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath)
    {
        if (!TryGetModList(out var modList))
            return new SourceTargetResolution(null, "penumbra_unavailable", "Could not retrieve the Penumbra mod list.");

        var registeredDirectory = modList.Keys.FirstOrDefault(directory =>
            string.Equals(directory, sourceModDirectory, StringComparison.OrdinalIgnoreCase));
        if (registeredDirectory is null)
            return new SourceTargetResolution(null, "source_mod_missing", "The source mod is no longer registered in Penumbra.");

        try
        {
            var modPath = _getModPath.Invoke(registeredDirectory, string.Empty);
            var configuredRoot = GetModDirectory();
            return ResolveSourceModTargetFromRoots(
                registeredDirectory,
                sourceFilePath,
                sourceModRootPath,
                targetRelativePath,
                modPath.Item1 is PenumbraApiEc.Success ? modPath.Item2 : null,
                string.IsNullOrWhiteSpace(configuredRoot)
                    ? null
                    : Path.Combine(configuredRoot, registeredDirectory));
        }
        catch (Exception e)
        {
            return new SourceTargetResolution(
                null,
                "destination_unavailable",
                $"Could not validate the original model destination: {e.Message}");
        }
    }

    internal static SourceTargetResolution ResolveSourceModTargetFromRoots(
        string registeredDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string? currentRegisteredRoot,
        string? configuredFallbackRoot)
    {
        try
        {
            var roots = new List<(string Root, bool Preferred)>();
            AddExportRoot(roots, currentRegisteredRoot, true, targetRelativePath);
            AddExportRoot(roots, configuredFallbackRoot, false, targetRelativePath);
            AddExportRoot(roots, sourceModRootPath, false, targetRelativePath);

            var relative = NormalizeRelativeModelPath(targetRelativePath);
            if (!string.IsNullOrWhiteSpace(targetRelativePath) && relative is null)
                return new SourceTargetResolution(
                    null,
                    "destination_unsafe",
                    "The authorized target-relative model path is unsafe.");
            if (relative is null && !string.IsNullOrWhiteSpace(sourceModRootPath) &&
                TryRelativeModelPath(sourceFilePath, sourceModRootPath, out var storedRelative))
                relative = storedRelative;
            if (relative is null)
            {
                foreach (var root in roots)
                {
                    if (TryRelativeModelPath(sourceFilePath, root.Root, out var candidateRelative))
                    {
                        relative = candidateRelative;
                        break;
                    }
                }
            }
            if (relative is null)
                return new SourceTargetResolution(
                    null,
                    "destination_unresolvable",
                    "The saved context has no safe model path relative to the registered mod.");

            var valid = new List<(SourceModTarget Target, bool Preferred)>();
            var sawMissingFolder = false;
            var sawUnsafe = false;
            // Once Penumbra resolves the registered directory key to an existing
            // root, that root is authoritative. If the reported root itself has
            // disappeared, retain the configured/captured roots as recovery
            // candidates; otherwise a stale IPC path makes every unchanged import
            // fail with destination_missing. A preferred root that still exists
            // but lacks the destination remains authoritative and cannot fall back.
            var resolutionRoots = roots.Any(candidate => candidate.Preferred && Directory.Exists(candidate.Root))
                ? roots.Where(candidate => candidate.Preferred)
                : roots;
            foreach (var candidate in resolutionRoots)
            {
                if (!Directory.Exists(candidate.Root))
                    continue;
                var target = Path.GetFullPath(Path.Combine(
                    candidate.Root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                var parent = Path.GetDirectoryName(target);
                if (parent is null || !IsPathWithin(target, candidate.Root))
                {
                    sawUnsafe = true;
                    continue;
                }
                if (!Directory.Exists(parent))
                {
                    sawMissingFolder = true;
                    continue;
                }
                if (HasReparsePointInPath(candidate.Root, parent) ||
                    (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
                {
                    sawUnsafe = true;
                    continue;
                }
                valid.Add((
                    new SourceModTarget(registeredDirectory, candidate.Root, target, relative),
                    candidate.Preferred));
            }

            var preferred = valid.Where(item => item.Preferred).Select(item => item.Target).FirstOrDefault();
            if (preferred is not null)
                return new SourceTargetResolution(preferred, "accepted", null);
            var distinct = valid
                .Select(item => item.Target)
                .DistinctBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinct.Length == 1)
                return new SourceTargetResolution(distinct[0], "accepted", null);
            if (distinct.Length > 1)
                return new SourceTargetResolution(
                    null,
                    "destination_ambiguous",
                    "Multiple registered mod roots contain the authorized destination.");
            if (sawUnsafe)
                return new SourceTargetResolution(
                    null,
                    "destination_unsafe",
                    "The original model path contains an unsupported reparse point or escapes the mod root.");
            return new SourceTargetResolution(
                null,
                "destination_missing",
                sawMissingFolder
                    ? "The original Penumbra destination folder is no longer available."
                    : "The registered Penumbra mod root is no longer available.");
        }
        catch (Exception e)
        {
            return new SourceTargetResolution(
                null,
                "destination_unavailable",
                $"Could not validate the original model destination: {e.Message}");
        }
    }

    /// <summary>Resolve the root currently registered for a Penumbra directory key.</summary>
    public string? GetRegisteredModPath(string modDirectory)
    {
        if (!IsSafeModName(modDirectory))
            return null;
        try
        {
            var result = _getModPath.Invoke(modDirectory, string.Empty);
            if (result.Item1 is not PenumbraApiEc.Success)
                return null;
            var path = NormalizePhysicalPath(result.Item2);
            if (path is null)
                return null;
            return string.Equals(Path.GetFileName(path), "Files", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(path)?.FullName ?? path
                : path;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve the registered path for {modDirectory}: {e.Message}");
            return null;
        }
    }

    private static void AddExportRoot(
        List<(string Root, bool Preferred)> roots,
        string? value,
        bool preferred,
        string? targetRelativePath)
    {
        var normalized = NormalizePhysicalPath(value);
        if (normalized is null)
            return;

        // Some Penumbra layouts expose the Files directory as the mod path.
        // The durable relative path is rooted at the folder containing mod
        // metadata, so normalize that representation back to the mod root.
        if (string.Equals(Path.GetFileName(normalized), "Files", StringComparison.OrdinalIgnoreCase) &&
            NormalizeRelativeModelPath(targetRelativePath)?.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) == true)
            normalized = Directory.GetParent(normalized)?.FullName ?? normalized;

        var existing = roots.FindIndex(item => string.Equals(item.Root, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (preferred && !roots[existing].Preferred)
                roots[existing] = (normalized, true);
            return;
        }
        roots.Add((normalized, preferred));
    }

    private static bool TryRelativeModelPath(string filePath, string root, out string relative)
    {
        relative = string.Empty;
        try
        {
            var fullFile = Path.GetFullPath(filePath);
            var fullRoot = Path.GetFullPath(root);
            if (!IsPathWithin(fullFile, fullRoot))
                return false;
            var candidate = Path.GetRelativePath(fullRoot, fullFile).Replace('\\', '/');
            var normalized = NormalizeRelativeModelPath(candidate);
            if (normalized is null)
                return false;
            relative = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeRelativeModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        return IsSafeRelativeModelPath(normalized) ? normalized : null;
    }

    /// <summary>Redraw the local player and the currently spawned entities they own.</summary>
    private string? RedrawPlayerOwnedEntitiesOnFramework()
    {
        if (_objects is null || _objects.LocalPlayer is not { Address: not 0 } localPlayer)
            return "The local player could not be found; player-owned redraw was skipped.";

        var targets = new HashSet<ushort> { localPlayer.ObjectIndex };
        var warnings = new List<string>();
        try
        {
            if (localPlayer.EntityId != 0)
            {
                var localOwnerId = localPlayer.EntityId;
                foreach (var candidate in _objects)
                {
                    if (candidate is null || candidate.Address == nint.Zero ||
                        candidate.ObjectIndex == localPlayer.ObjectIndex ||
                        candidate.OwnerId != localOwnerId)
                        continue;

                    if (candidate.ObjectKind is ObjectKind.Companion or ObjectKind.Mount or ObjectKind.FollowMount ||
                        candidate is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Pet })
                        targets.Add(candidate.ObjectIndex);
                }
            }
            else
            {
                warnings.Add("The local player entity ID was unavailable; only the player was redrawn.");
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not enumerate player-owned entities for redraw.");
            warnings.Add("Could not enumerate all player-owned entities; continuing with the targets found.");
        }

        var failed = 0;
        foreach (var objectIndex in targets)
        {
            try
            {
                _redrawObject.Invoke(objectIndex);
            }
            catch (Exception e)
            {
                failed++;
                _log.Error(e, "Penumbra redraw failed for player-owned object {ObjectIndex}.", objectIndex);
            }
        }

        if (failed > 0)
            warnings.Add($"Penumbra redraw failed for {failed} player-owned object(s).");
        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private static string? WriteModelToOriginalLocation(
        string modFolder,
        string targetFile,
        string exportedFile,
        bool backupExisting = false)
    {
        try
        {
            var fullTarget = Path.GetFullPath(targetFile);
            var parent = Path.GetDirectoryName(fullTarget);
            if (parent is null || !IsPathWithin(fullTarget, modFolder) ||
                !string.Equals(Path.GetExtension(fullTarget), ".mdl", StringComparison.OrdinalIgnoreCase) ||
                HasReparsePointInPath(modFolder, parent) ||
                (File.Exists(fullTarget) && (File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0))
                return "The original mod destination is unsafe.";

            Directory.CreateDirectory(parent);
            if (backupExisting && File.Exists(fullTarget))
                CreateModelBackup(fullTarget);
            var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(exportedFile, temporary, true);
                File.Move(temporary, fullTarget, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }

            return null;
        }
        catch (Exception e)
        {
            return $"Could not write the original model file: {e.Message}";
        }
    }

    public IReadOnlyList<PenumbraMod> GetMods()
        => GetModList()
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new PenumbraMod(pair.Key, pair.Value))
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record ModScanRequest(PenumbraMod Mod, string[] CandidateRoots);

    /// <summary>Resolve Penumbra state on the framework thread, then scan files on a worker.</summary>
    public async Task<PenumbraModSnapshot?> GetModResourcesAsync(
        string modDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeModName(modDirectory))
            return null;

        try
        {
            var request = await _framework.RunOnFrameworkThread(
                () => ResolveModScanOnFramework(modDirectory)).ConfigureAwait(false);
            if (request is null)
                return null;
            return await Task.Run(() => ScanModResources(request, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not read Penumbra mod resources: {e.Message}");
            return null;
        }
    }

    private ModScanRequest? ResolveModScanOnFramework(string modDirectory)
    {
        var mod = GetMods().FirstOrDefault(item =>
            string.Equals(item.Directory, modDirectory, StringComparison.OrdinalIgnoreCase));
        if (mod is null)
            return null;

        var candidateRoots = new List<string>();
        var pathResult = _getModPath.Invoke(mod.Directory, string.Empty);
        if (pathResult.Item1 is PenumbraApiEc.Success)
            AddCandidateRoot(candidateRoots, pathResult.Item2);

        // GetModPath can point at a manually configured location. Keep the
        // standard Penumbra root as a fallback for older/API-incompatible installs.
        var modDirectoryRoot = GetModDirectory();
        if (!string.IsNullOrWhiteSpace(modDirectoryRoot))
            AddCandidateRoot(candidateRoots, Path.Combine(modDirectoryRoot, mod.Directory));
        return new ModScanRequest(mod, candidateRoots.ToArray());
    }

    private PenumbraModSnapshot ScanModResources(ModScanRequest request, CancellationToken cancellationToken)
    {
        var mod = request.Mod;
        try
        {
            foreach (var root in request.CandidateRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    continue;

                var standardFilesRoot = Path.Combine(root, "Files");
                string scanRoot;
                string sourceRoot;
                string relativePrefix;
                if (string.Equals(Path.GetFileName(root), "Files", StringComparison.OrdinalIgnoreCase))
                {
                    // Penumbra exposes the Files directory itself; treat its parent as the
                    // durable mod root and prefix every relative path with "Files/".
                    sourceRoot = Directory.GetParent(root)?.FullName ?? root;
                    scanRoot = root;
                    relativePrefix = "Files/";
                }
                else if (Directory.Exists(standardFilesRoot))
                {
                    // Standard Penumbra layout: the mod root has a Files/ subdirectory that
                    // contains every game resource. The relative path must mirror that
                    // prefix so the registry can reproduce sourceModRootPath + relativePath
                    // == targetFilePath when a Mod Browser import becomes a Quick Export.
                    sourceRoot = root;
                    scanRoot = standardFilesRoot;
                    relativePrefix = "Files/";
                }
                else
                {
                    sourceRoot = root;
                    scanRoot = root;
                    relativePrefix = string.Empty;
                }

                if (!Directory.Exists(scanRoot) || HasReparsePointInPath(sourceRoot, scanRoot))
                    continue;

                var mappings = ReadModMappings(sourceRoot);
                var resources = new List<PenumbraModResource>();
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var file in Directory.EnumerateFiles(scanRoot, "*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSafeModResourceFile(sourceRoot, file))
                        continue;

                    var relativePath = Path.GetRelativePath(scanRoot, file).Replace('\\', '/');
                    var modPath = relativePrefix + relativePath;
                    var gamePath = CanonicalGamePathFor(modPath, mappings.GamePaths);
                    var extension = Path.GetExtension(relativePath);
                    if (!extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".atex", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase))
                        continue;

                    resources.Add(new PenumbraModResource(
                        gamePath,
                        file,
                        modPath,
                        OptionMappingFor(modPath, mappings.OptionLabels)));
                }

                if (resources.Count > 0)
                    return new PenumbraModSnapshot(
                        mod.Directory,
                        mod.Name,
                        sourceRoot,
                        resources.OrderBy(resource => resource.GamePath, StringComparer.OrdinalIgnoreCase).ToArray());
            }

            return new PenumbraModSnapshot(mod.Directory, mod.Name, request.CandidateRoots.FirstOrDefault() ?? string.Empty, Array.Empty<PenumbraModResource>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not read Penumbra mod resources: {e.Message}");
            return new PenumbraModSnapshot(mod.Directory, mod.Name, request.CandidateRoots.FirstOrDefault() ?? string.Empty, Array.Empty<PenumbraModResource>());
        }
    }

    /// <summary>Restore a timestamped MDL backup beside an authorized source model.</summary>
    public async Task<ExportResult> RestoreSourceBackupAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        string backupName)
    {
        if (!IsSafeModName(sourceModDirectory) || !IsSafeGamePath(sourceGamePath) ||
            !IsSafeLocalModelPath(sourceFilePath) || !TryGetBackupOriginal(backupName, out var originalName))
            return new ExportResult(false, "invalid_restore", "The backup restore request is invalid.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        string? committedTarget = null;
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => ResolveSourceModTargetOnFramework(
                    sourceModDirectory,
                    sourceFilePath,
                    sourceModRootPath,
                    targetRelativePath)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new ExportResult(
                    false,
                    resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.");

            var parent = Path.GetDirectoryName(resolved.Target.FilePath);
            if (parent is null || !string.Equals(Path.GetExtension(originalName), ".mdl", StringComparison.OrdinalIgnoreCase))
                return new ExportResult(false, "invalid_restore", "The backup target is invalid.");
            var backupPath = Path.Combine(parent, backupName);
            var targetPath = Path.Combine(parent, originalName);
            if (!IsPathWithin(backupPath, resolved.Target.Folder) ||
                !IsPathWithin(targetPath, resolved.Target.Folder) ||
                !string.Equals(Path.GetDirectoryName(backupPath), parent, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
                return new ExportResult(false, "backup_missing", "The backup is not in the authorized model folder.");
            if (HasReparsePointInPath(resolved.Target.Folder, parent) ||
                (File.Exists(backupPath) && (File.GetAttributes(backupPath) & FileAttributes.ReparsePoint) != 0) ||
                (File.Exists(targetPath) && (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0))
                return new ExportResult(false, "destination_unsafe", "The backup target contains an unsupported reparse point.");

            var writeError = RestoreModelBackup(targetPath, backupPath);
            if (writeError is not null)
                return new ExportResult(false, "restore_write_failed", writeError);
            committedTarget = targetPath;

            var warnings = new List<string>();

            var reloadError = await _framework.RunOnFrameworkThread(
                () => ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                warnings.Add(reloadError.Message);
            else
            {
                var redrawWarning = await _framework.RunOnFrameworkThread(
                    RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                if (redrawWarning is not null)
                    warnings.Add(redrawWarning);
            }

            var code = warnings.Count == 0 ? "backup_restored" : "backup_restored_with_warnings";
            var message = warnings.Count == 0
                ? $"Restored {originalName} and reloaded {resolved.Target.Directory}."
                : $"Restored {originalName}; {warnings.Count} follow-up warning(s).";
            return new ExportResult(true, code, message, warnings, targetPath);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to restore a model backup.");
            if (committedTarget is not null)
                return new ExportResult(
                    true,
                    "backup_restored_with_warnings",
                    $"Restored {originalName}; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, "restore_failed", $"Failed to restore the model backup: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private static void CreateModelBackup(string targetFile)
    {
        var directory = Path.GetDirectoryName(targetFile)!;
        var original = Path.GetFileName(targetFile);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss.ffffff'Z'");
            var backup = Path.Combine(directory, $"{original}.{stamp}.bak");
            if (File.Exists(backup))
                continue;
            File.Copy(targetFile, backup, false);
            return;
        }

        throw new IOException("Could not allocate a unique model backup filename.");
    }

    private static string? RestoreModelBackup(string targetFile, string backupFile)
    {
        try
        {
            if (File.Exists(targetFile))
                CreateModelBackup(targetFile);
            var temporary = Path.Combine(Path.GetDirectoryName(targetFile)!, $".instant-edit-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(backupFile, temporary, false);
                File.Move(temporary, targetFile, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return null;
        }
        catch (Exception e)
        {
            return $"Could not restore the model backup: {e.Message}";
        }
    }

    private static bool TryGetBackupOriginal(string? backupName, out string originalName)
    {
        originalName = "";
        if (string.IsNullOrWhiteSpace(backupName) || backupName.Length > 512 ||
            backupName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || backupName.Contains('/') || backupName.Contains('\\'))
            return false;
        var match = Regex.Match(
            backupName,
            @"^(?<original>.+\.mdl)\.\d{8}T\d{6}\.\d{6}Z\.bak$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            originalName = match.Groups["original"].Value;
        else if (backupName.EndsWith(".mdl.bak", StringComparison.OrdinalIgnoreCase))
            originalName = backupName[..^4];
        else
            return false;
        return originalName.Length <= 255 && originalName is not ".mdl" and not "." and not "..";
    }

    private ExportResult? ReloadModOnFramework(string modName)
    {
        PenumbraApiEc modResult;
        try
        {
            modResult = _reloadMod.Invoke(modName);
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra failed while reloading the XIV Instant Edit mod.");
            return new ExportResult(false, $"Penumbra add/reload failed: {e.Message}");
        }

        if (modResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected the mod ({modResult}).");

        return null;
    }

    private async Task<ExportResult?> AddNewModAsync(string modName)
    {
        var added = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventSubscriber<string>? subscription = null;

        try
        {
            subscription = ModAdded.Subscriber(_pi, directory =>
            {
                if (string.Equals(directory, modName, StringComparison.OrdinalIgnoreCase))
                    added.TrySetResult(directory);
            });
            subscription.Enable();

            // AddMod only queues discovery. Do not wait for its event from this
            // callback; the continuation waits on a thread-pool context instead.
            var addResult = await _framework.RunOnFrameworkThread(
                () => AddModOnFramework(modName)).ConfigureAwait(false);
            if (addResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                return new ExportResult(false, $"Penumbra rejected adding the mod ({addResult}).");

            try
            {
                await added.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new ExportResult(false, $"Timed out waiting for Penumbra to add '{modName}'.");
            }

            return null;
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra failed while adding the XIV Instant Edit mod.");
            return new ExportResult(false, $"Penumbra add failed: {e.Message}");
        }
        finally
        {
            if (subscription is not null)
            {
                try
                {
                    subscription.Disable();
                }
                catch (Exception e)
                {
                    _log.Debug($"Could not disable Penumbra mod-added subscription: {e.Message}");
                }

                try
                {
                    subscription.Dispose();
                }
                catch (Exception e)
                {
                    _log.Debug($"Could not dispose Penumbra mod-added subscription: {e.Message}");
                }
            }
        }
    }

    private PenumbraApiEc AddModOnFramework(string modName)
        => _addMod.Invoke(modName);

    private ExportResult ConfigureModOnFramework(
        string modName,
        int objectIndex,
        bool setPriority = true,
        int priority = int.MaxValue)
    {

        (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection) collection;
        try
        {
            collection = _getCollectionForObject.Invoke(objectIndex);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not retrieve the target Penumbra collection.");
            return new ExportResult(false, $"Could not retrieve the target collection: {e.Message}");
        }

        if (!collection.ObjectValid || collection.EffectiveCollection.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(collection.EffectiveCollection.Name))
            return new ExportResult(false, "The target object has no valid Penumbra collection.");

        PenumbraApiEc enabledResult;
        try
        {
            enabledResult = _trySetMod.Invoke(
                collection.EffectiveCollection.Id,
                modName,
                true,
                modName);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not enable the XIV Instant Edit mod in Penumbra.");
            return new ExportResult(false, $"Penumbra enable failed: {e.Message}");
        }

        if (enabledResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected enabling the mod ({enabledResult}).");

        if (setPriority)
        {
            PenumbraApiEc priorityResult;
            try
            {
                priorityResult = _trySetModPriority.Invoke(
                    collection.EffectiveCollection.Id,
                    modName,
                    priority,
                    modName);
            }
            catch (Exception e)
            {
                _log.Error(e, "Could not prioritize the XIV Instant Edit mod in Penumbra.");
                return new ExportResult(false, $"Penumbra priority failed: {e.Message}");
            }

            if (priorityResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                return new ExportResult(false, $"Penumbra rejected the mod priority ({priorityResult}).");
        }

        try
        {
            var redrawWarning = RedrawPlayerOwnedEntitiesOnFramework();
            return redrawWarning is null
                ? new ExportResult(true, $"Applied {modName} to {collection.EffectiveCollection.Name} ({objectIndex}).")
                : new ExportResult(
                    true,
                    "export_applied_with_warnings",
                    $"Applied {modName} to {collection.EffectiveCollection.Name} ({objectIndex}).",
                    [redrawWarning]);
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra redraw failed after applying export.");
            return new ExportResult(false, $"Penumbra redraw failed: {e.Message}");
        }
    }

    private bool TryGetModList(out Dictionary<string, string> modList)
    {
        try
        {
            modList = _getModList.Invoke() ?? new Dictionary<string, string>();
            return true;
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not retrieve the Penumbra mod list for export.");
            modList = new Dictionary<string, string>();
            return false;
        }
    }


    private sealed record VariantOptionResolution(string? FilePath, string Code, string? Error);

    private static IReadOnlyList<VariantGroupTarget> ReadVariantTargets(string modFolder, string sourceGamePath)
    {
        var groups = new List<(JsonObject Group, string? LegacyFileName)>();
        var meta = LoadJsonObjectStrict(Path.Combine(modFolder, "meta.json"));
        var fileVersion = meta["FileVersion"] is JsonValue versionValue && versionValue.TryGetValue<int>(out var version)
            ? version : 3;
        if (fileVersion >= 4)
            groups.AddRange((meta["Groups"] as JsonArray ?? []).OfType<JsonObject>().Select(group => (group, (string?)null)));
        else
            groups.AddRange(Directory.EnumerateFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => (Group: LoadJsonObjectStrict(path), LegacyFileName: (string?)Path.GetFileName(path))));

        var result = new List<VariantGroupTarget>();
        foreach (var (group, legacyFileName) in groups)
        {
            if (!string.Equals(JsonString(group["Type"]), "Single", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(JsonString(group["Name"])) ||
                group["Options"] is not JsonArray options)
                continue;
            var groupId = ReadGuid(group["Id"]);
            if (groupId is null && legacyFileName is null)
                continue;
            var targets = new List<VariantOptionTarget>();
            for (var optionIndex = 0; optionIndex < options.Count; ++optionIndex)
            {
                if (options[optionIndex] is not JsonObject option || option["Files"] is not JsonObject files)
                    continue;
                var mapping = files.FirstOrDefault(pair => SameGamePath(pair.Key, sourceGamePath));
                if (!TryNormalizeRelativeModPath(JsonString(mapping.Value), out var modelPath))
                    continue;
                var optionId = ReadGuid(option["Id"]);
                var selector = groupId is Guid groupGuid && optionId is Guid optionGuid
                    ? $"option:{groupGuid:D}:{optionGuid:D}"
                    : $"legacy-option:{legacyFileName}:{optionIndex}";
                targets.Add(new VariantOptionTarget(selector,
                    JsonString(option["Name"]) ?? "Unnamed Option", modelPath));
            }
            if (targets.Count > 0)
            {
                var selector = groupId is Guid groupGuid
                    ? $"group:{groupGuid:D}"
                    : $"legacy-group:{legacyFileName}";
                result.Add(new VariantGroupTarget(selector, JsonString(group["Name"])!, targets));
            }
        }
        return result;
    }

    private static VariantOptionResolution ResolveVariantOptionTarget(
        string modFolder, string sourceGamePath, string? targetId)
    {
        try
        {
            JsonObject? group;
            JsonObject? option;
            if (TryParseOptionTargetId(targetId, out var groupId, out var optionId))
            {
                group = ReadAllVariantGroups(modFolder).FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == groupId);
                option = group?["Options"] is JsonArray options
                    ? options.OfType<JsonObject>().FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == optionId)
                    : null;
            }
            else if (TryParseLegacyOptionTargetId(targetId, out var fileName, out var optionIndex) &&
                     TryLoadLegacyGroup(modFolder, fileName, out group) &&
                     group["Options"] is JsonArray options && optionIndex < options.Count)
            {
                option = options[optionIndex] as JsonObject;
            }
            else
            {
                return new VariantOptionResolution(null, "invalid_variant_target", "The selected Penumbra option is invalid.");
            }
            if (option?["Files"] is not JsonObject files)
                return new VariantOptionResolution(null, "stale_variant_target", "The selected Penumbra option no longer exists.");
            var mapping = files.FirstOrDefault(pair => SameGamePath(pair.Key, sourceGamePath));
            if (!TryNormalizeRelativeModPath(JsonString(mapping.Value), out var relative))
                return new VariantOptionResolution(null, "stale_variant_target", "The selected option no longer replaces this model.");
            var path = Path.GetFullPath(Path.Combine(modFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(path, modFolder))
                return new VariantOptionResolution(null, "destination_unsafe", "The selected option has an unsafe model path.");
            return new VariantOptionResolution(path, "accepted", null);
        }
        catch (Exception e)
        {
            return new VariantOptionResolution(null, "variant_target_unavailable", $"Could not read the selected Penumbra option: {e.Message}");
        }
    }

    private static string? ValidateVariantGroupTarget(string modFolder, string sourceGamePath, string? targetId)
    {
        JsonObject? group;
        if (TryParseGroupTargetId(targetId, out var groupId))
            group = ReadAllVariantGroups(modFolder).FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == groupId);
        else if (TryParseLegacyGroupTargetId(targetId, out var fileName) && TryLoadLegacyGroup(modFolder, fileName, out group))
        {
            // Resolved from the legacy file selected by the Blender tree.
        }
        else
            return "The selected Penumbra group is invalid.";
        if (group is null || !string.Equals(JsonString(group["Type"]), "Single", StringComparison.OrdinalIgnoreCase) ||
            !GroupHasGamePath(group, sourceGamePath))
            return "The selected Penumbra group no longer contains a replacement for this model.";
        return null;
    }

    private static IReadOnlyList<JsonObject> ReadAllVariantGroups(string modFolder)
    {
        var meta = LoadJsonObjectStrict(Path.Combine(modFolder, "meta.json"));
        var version = meta["FileVersion"] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : 3;
        return version >= 4
            ? (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>().ToArray()
            : Directory.EnumerateFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly)
                .Select(LoadJsonObjectStrict).ToArray();
    }

    private static bool TryParseOptionTargetId(string? value, out Guid groupId, out Guid optionId)
    {
        groupId = Guid.Empty;
        optionId = Guid.Empty;
        var parts = value?.Split(':');
        return parts is ["option", var group, var option] &&
               Guid.TryParse(group, out groupId) && Guid.TryParse(option, out optionId);
    }

    private static bool TryParseGroupTargetId(string? value, out Guid groupId)
    {
        groupId = Guid.Empty;
        var parts = value?.Split(':');
        return parts is ["group", var group] && Guid.TryParse(group, out groupId);
    }

    private static bool TryParseLegacyGroupTargetId(string? value, out string fileName)
    {
        fileName = "";
        var parts = value?.Split(':');
        return parts is ["legacy-group", var file] && IsSafeLegacyGroupFileName(file, out fileName);
    }

    private static bool TryParseLegacyOptionTargetId(string? value, out string fileName, out int optionIndex)
    {
        fileName = "";
        optionIndex = -1;
        var parts = value?.Split(':');
        return parts is ["legacy-option", var file, var index] &&
               IsSafeLegacyGroupFileName(file, out fileName) && int.TryParse(index, out optionIndex) && optionIndex >= 0;
    }

    private static bool TryLoadLegacyGroup(string modFolder, string fileName, out JsonObject group)
    {
        group = null!;
        if (!IsSafeLegacyGroupFileName(fileName, out var safeFileName))
            return false;
        var path = Path.Combine(modFolder, safeFileName);
        if (!File.Exists(path))
            return false;
        group = LoadJsonObjectStrict(path);
        return true;
    }

    private static bool IsSafeLegacyGroupFileName(string? value, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            !value.StartsWith("group_", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        fileName = value;
        return true;
    }

    private static bool TryNormalizeRelativeModPath(string? value, out string path)
    {
        path = (value ?? "").Replace('\\', '/');
        return IsSafeRelativeModPath(path);
    }

    /// <summary>
    /// Create or update a Penumbra group that redirects the original model path
    /// to the newly exported sibling. Both legacy v3 group files and current v4
    /// embedded groups are supported without rewriting unrelated mod data.
    /// </summary>
    private static string? WriteVariantGroup(
        string modFolder,
        string sourceGamePath,
        string relativeVariantPath,
        string variantName,
        string variantGroupName)
    {
        try
        {
            relativeVariantPath = relativeVariantPath.Replace('\\', '/');
            if (!IsSafeGamePath(sourceGamePath) || !IsSafeRelativeModPath(relativeVariantPath) ||
                !IsSafeVariantName(variantName) || !IsSafeVariantGroupName(variantGroupName))
                return "invalid_penumbra_variant";

            // A group name is reusable only for a Single group whose existing
            // options all redirect this same game path. An incompatible match
            // is rejected so user-owned group semantics are never rewritten.
            var marker = VariantGroupDescriptionPrefix + variantGroupName + " -> " + sourceGamePath;
            var metaPath = Path.Combine(modFolder, "meta.json");
            var meta = LoadJsonObjectStrict(metaPath);
            var fileVersion = meta["FileVersion"] is JsonValue versionValue &&
                              versionValue.TryGetValue<int>(out var version)
                ? version
                : 3;

            if (fileVersion >= 4)
            {
                var groups = meta["Groups"] as JsonArray;
                if (groups is null)
                {
                    groups = new JsonArray();
                    meta["Groups"] = groups;
                }
                JsonObject? embeddedExistingGroup = null;
                var existingIndex = -1;
                var embeddedNameConflict = false;
                var embeddedHighestOtherPriority = 0;
                for (var index = 0; index < groups.Count; ++index)
                {
                    if (groups[index] is not JsonObject group)
                        continue;
                    var sameName = string.Equals(
                        JsonString(group["Name"]), variantGroupName, StringComparison.OrdinalIgnoreCase);
                    if (sameName && IsReusableVariantGroup(group, sourceGamePath))
                    {
                        embeddedExistingGroup = group;
                        existingIndex = index;
                        continue;
                    }
                    if (sameName)
                        embeddedNameConflict = true;

                    embeddedHighestOtherPriority = Math.Max(embeddedHighestOtherPriority, JsonInt(group["Priority"]));
                }

                if (embeddedExistingGroup is null && embeddedNameConflict)
                    return "An existing Penumbra group with this name is not a compatible Single group for this model.";
                if (embeddedHighestOtherPriority == int.MaxValue)
                    return "penumbra_group_priority_exhausted";
                var embeddedGroupJson = BuildVariantGroup(
                    embeddedExistingGroup,
                    marker,
                    sourceGamePath,
                    relativeVariantPath,
                    variantName,
                    variantGroupName,
                    embeddedHighestOtherPriority + 1);
                if (existingIndex >= 0)
                    groups[existingIndex] = embeddedGroupJson;
                else
                    groups.Add(embeddedGroupJson);
                meta["LastWrite"] = DateTime.UtcNow;
                WriteJsonAtomic(metaPath, meta);
                return null;
            }

            var groupFiles = Directory.EnumerateFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string? existingPath = null;
            JsonObject? existingGroup = null;
            var nameConflict = false;
            var highestOtherPriority = 0;
            var highestFileIndex = 0;

            foreach (var groupPath in groupFiles)
            {
                var fileName = Path.GetFileName(groupPath);
                if (TryReadGroupFileIndex(fileName, out var fileIndex))
                    highestFileIndex = Math.Max(highestFileIndex, fileIndex);

                var group = LoadJsonObjectStrict(groupPath);
                var sameName = string.Equals(
                    JsonString(group["Name"]), variantGroupName, StringComparison.OrdinalIgnoreCase);
                var isExisting = sameName && IsReusableVariantGroup(group, sourceGamePath);
                if (isExisting)
                {
                    existingPath = groupPath;
                    existingGroup = group;
                    continue;
                }
                if (sameName)
                    nameConflict = true;

                highestOtherPriority = Math.Max(highestOtherPriority, JsonInt(group["Priority"]));
            }

            if (existingGroup is null && nameConflict)
                return "An existing Penumbra group with this name is not a compatible Single group for this model.";
            if (highestOtherPriority == int.MaxValue)
                return "penumbra_group_priority_exhausted";

            var groupJson = BuildVariantGroup(
                existingGroup,
                marker,
                sourceGamePath,
                relativeVariantPath,
                variantName,
                variantGroupName,
                highestOtherPriority + 1);

            var outputGroupPath = existingPath ?? Path.Combine(
                modFolder,
                $"group_{highestFileIndex + 1:D3}_instant_edit_{SanitizeGroupFileName(variantGroupName)}.json");
            WriteJsonAtomic(outputGroupPath, groupJson);
            return null;
        }
        catch (Exception e)
        {
            return $"penumbra_group_write_failed: {e.Message}";
        }
    }

    private static JsonObject BuildVariantGroup(
        JsonObject? existingGroup,
        string marker,
        string sourceGamePath,
        string relativeVariantPath,
        string variantName,
        string variantGroupName,
        int priority)
    {
        var groupId = ReadGuid(existingGroup?["Id"]) ?? Guid.NewGuid();
        var options = existingGroup?["Options"]?.DeepClone() as JsonArray ?? new JsonArray();
        if (existingGroup is null)
        {
            options.Insert(0, new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = "None",
            });
        }

        var variantOption = options
            .OfType<JsonObject>()
            .FirstOrDefault(option =>
                string.Equals(JsonString(option["Name"]), variantName, StringComparison.OrdinalIgnoreCase));
        if (variantOption is null)
        {
            variantOption = new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = variantName,
                ["Files"] = new JsonObject
                {
                    [sourceGamePath] = relativeVariantPath,
                },
            };
            options.Add(variantOption);
        }
        else
        {
            var files = variantOption["Files"]?.DeepClone() as JsonObject ?? new JsonObject();
            files[sourceGamePath] = relativeVariantPath;
            variantOption["Files"] = files;
        }
        var selectedIndex = options.IndexOf(variantOption);
        if (selectedIndex < 0)
            throw new InvalidOperationException("The exported variant option was not added to its group.");

        return new JsonObject
        {
            ["Version"] = 0,
            ["Type"] = "Single",
            ["Id"] = groupId,
            ["Name"] = variantGroupName,
            ["Description"] = marker,
            ["Priority"] = priority,
            ["DefaultSettings"] = selectedIndex,
            ["Options"] = options,
        };
    }

    private static bool IsReusableVariantGroup(JsonObject group, string sourceGamePath)
        => string.Equals(JsonString(group["Type"]), "Single", StringComparison.OrdinalIgnoreCase) &&
           GroupHasGamePath(group, sourceGamePath);

    private static bool GroupHasGamePath(JsonObject group, string sourceGamePath)
    {
        if (group["Options"] is not JsonArray options)
            return false;

        foreach (var option in options.OfType<JsonObject>())
        {
            if (option["Files"] is not JsonObject files)
                continue;

            if (files.Any(file => SameGamePath(file.Key, sourceGamePath)))
                return true;
        }

        return false;
    }

    private static bool SameGamePath(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string? JsonString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int JsonInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static bool TryReadGroupFileIndex(string fileName, out int index)
    {
        index = 0;
        if (!fileName.StartsWith("group_", StringComparison.OrdinalIgnoreCase) || fileName.Length < 10)
            return false;
        var separator = fileName.IndexOf('_', 6);
        if (separator < 0)
            return false;
        return int.TryParse(fileName.AsSpan(6, separator - 6), out index) && index > 0;
    }

    private static Guid? ReadGuid(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var guid)
            ? guid
            : null;

    private static string SanitizeGroupFileName(string value)
    {
        var safe = new string(value.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray()).Trim('_');
        return string.IsNullOrEmpty(safe) ? "variant" : safe;
    }

    internal static string FormatMashupDescription(IReadOnlyList<MashupContributor> contributors)
    {
        var names = contributors
            .Select(item => item.Context.SourceModName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Replace("\"", "'", StringComparison.Ordinal))
            .ToArray();
        return $"Mashup created by XIV Instant Edit from {string.Join(" and ", names.Select(name => $"\"{name}\""))}.";
    }

    private static bool HasReparsePointInPath(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current.Length >= fullRoot.Length)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        return false;
    }

    private static JsonObject LoadDefaultMod(string defaultPath)
        => LoadJsonObject(defaultPath);

    private static JsonObject LoadJsonObject(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            // A malformed file cannot be merged safely; the exported mapping will
            // still be written, while unrelated valid files on disk remain intact.
            return new JsonObject();
        }
    }

    private static JsonObject LoadJsonObjectStrict(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required Penumbra metadata file was not found.", path);
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"Penumbra metadata is not a JSON object: {path}");
    }

    private sealed record ModMappings(
        IReadOnlyDictionary<string, string?> GamePaths,
        IReadOnlyDictionary<string, string> OptionLabels);

    private static ModMappings ReadModMappings(string modRoot)
    {
        var gamePathsByModPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var labelsByPath = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddFileMappings(JsonNode? container, string label)
        {
            if (container is not JsonObject value || value["Files"] is not JsonObject files)
                return;

            foreach (var file in files)
            {
                var relativePath = JsonString(file.Value);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var gamePath = NormalizeCanonicalGamePath(file.Key);
                if (gamePath.Length > 0)
                {
                    foreach (var key in ModPathKeys(relativePath))
                    {
                        if (!gamePathsByModPath.TryGetValue(key, out var existing))
                            gamePathsByModPath[key] = gamePath;
                        else if (existing is not null && !SameGamePath(existing, gamePath))
                            gamePathsByModPath[key] = null;
                    }
                }

                foreach (var key in ModPathKeys(relativePath))
                {
                    if (!labelsByPath.TryGetValue(key, out var labels))
                        labelsByPath[key] = labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    labels.Add(label);
                }
            }
        }

        void AddGroup(JsonNode? group)
        {
            if (group is not JsonObject value)
                return;

            var groupName = JsonString(value["Name"]);
            if (string.IsNullOrWhiteSpace(groupName))
                groupName = "Unnamed group";

            if (value["Options"] is JsonArray options)
            {
                foreach (var option in options.OfType<JsonObject>())
                {
                    var optionName = JsonString(option["Name"]);
                    var label = string.IsNullOrWhiteSpace(optionName)
                        ? groupName
                        : $"{groupName}: {optionName}";
                    AddFileMappings(option, label);
                }
            }

            if (value["Containers"] is JsonArray containers)
            {
                foreach (var container in containers.OfType<JsonObject>())
                {
                    var containerName = JsonString(container["Name"]);
                    var label = string.IsNullOrWhiteSpace(containerName)
                        ? groupName
                        : $"{groupName}: {containerName}";
                    AddFileMappings(container, label);
                }
            }
        }

        AddFileMappings(LoadJsonObject(Path.Combine(modRoot, "default_mod.json")), "Default");

        var meta = LoadJsonObject(Path.Combine(modRoot, "meta.json"));
        AddFileMappings(meta["DefaultData"], "Default");
        if (meta["Groups"] is JsonArray metaGroups)
            foreach (var group in metaGroups)
                AddGroup(group);

        try
        {
            foreach (var groupPath in Directory.EnumerateFiles(modRoot, "group_*.json", SearchOption.TopDirectoryOnly))
                AddGroup(LoadJsonObject(groupPath));
        }
        catch
        {
            // A missing or inaccessible legacy group file should not hide the
            // resources that were successfully discovered from the mod folder.
        }

        return new ModMappings(
            gamePathsByModPath,
            labelsByPath.ToDictionary(
                pair => pair.Key,
                pair => string.Join(" | ", pair.Value),
                StringComparer.OrdinalIgnoreCase));
    }

    private static string CanonicalGamePathFor(
        string relativePath,
        IReadOnlyDictionary<string, string?> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var gamePath) && gamePath is not null)
                return gamePath;

        return NormalizeCanonicalGamePath(relativePath);
    }

    private static string NormalizeCanonicalGamePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..];
        return normalized;
    }

    private static string OptionMappingFor(string relativePath, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var label))
                return label;
        return "Unmapped";
    }

    private static IEnumerable<string> ModPathKeys(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.Length == 0)
            yield break;

        yield return normalized;
        if (normalized.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            yield return normalized[6..];
        else
            yield return "Files/" + normalized;
    }

    private static void WriteJsonAtomic(string path, JsonObject value)
    {
        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, value.ToJsonString(JsonOpts));
            File.Move(tempPath, path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private sealed record CleanupMapping(
        JsonObject Files,
        string GamePath,
        string OldRelativePath,
        string CanonicalPath);

    private sealed record CleanupJsonFile(string Path, JsonObject Value);

    private sealed record ModCleanupResult(
        IReadOnlyList<string> Warnings,
        ModPathRemap? PathRemap);

    /// <summary>
    /// Normalize and deduplicate a committed mod without relying on a
    /// Penumbra-version-specific IPC endpoint. The complete directory is
    /// snapshotted before mutation so failures leave the committed mashup
    /// exactly as it was before cleanup started.
    /// </summary>
    private static ModCleanupResult NormalizeAndDeduplicateMod(string modRoot, string modDirectory)
    {
        try
        {
            var root = NormalizePhysicalPath(modRoot)
                ?? throw new InvalidDataException("The Penumbra mod root is invalid.");
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Penumbra mod root is unavailable or unsafe.");

            var jsonFiles = new List<CleanupJsonFile>();
            var mappings = new List<CleanupMapping>();

            void AddJsonFile(string path, JsonObject value)
            {
                if (jsonFiles.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return;
                jsonFiles.Add(new CleanupJsonFile(path, value));
            }

            void AddContainer(JsonNode? container, string? groupName, string? optionName, string jsonPath)
            {
                if (container is not JsonObject value || value["Files"] is not JsonObject files)
                    return;

                var group = SafeModPathSegment(groupName);
                var option = SafeModPathSegment(optionName);
                foreach (var pair in files.ToArray())
                {
                    var oldRelative = JsonString(pair.Value);
                    if (string.IsNullOrWhiteSpace(oldRelative))
                        continue;
                    if (!TryNormalizeModContentPath(oldRelative, out var normalizedOld))
                        throw new InvalidDataException($"The mapping {jsonPath}:{pair.Key} has an unsafe content path.");

                    var gamePath = NormalizeCanonicalGamePath(pair.Key);
                    if (!TryNormalizeModContentPath(gamePath, out gamePath))
                        throw new InvalidDataException($"The mapping {jsonPath}:{pair.Key} has an unsafe game path.");

                    var canonical = BuildCanonicalModPath(group, option, gamePath);
                    mappings.Add(new CleanupMapping(files, pair.Key, normalizedOld, canonical));
                }
            }

            void AddGroup(JsonNode? group, string jsonPath)
            {
                if (group is not JsonObject value)
                    return;
                var groupName = JsonString(value["Name"]);
                if (string.IsNullOrWhiteSpace(groupName))
                    groupName = "Unnamed group";

                if (value["Files"] is JsonObject)
                    AddContainer(value, groupName, "None", jsonPath);
                if (value["Options"] is JsonArray options)
                    for (var index = 0; index < options.Count; ++index)
                    {
                        var option = options[index] as JsonObject;
                        AddContainer(option, groupName, JsonString(option?["Name"]) ?? $"Option {index + 1}",
                            $"{jsonPath}.Options[{index}]");
                    }
                if (value["Containers"] is JsonArray containers)
                    for (var index = 0; index < containers.Count; ++index)
                    {
                        var container = containers[index] as JsonObject;
                        AddContainer(container, groupName, JsonString(container?["Name"]) ?? $"Container {index + 1}",
                            $"{jsonPath}.Containers[{index}]");
                    }
            }

            var defaultPath = Path.Combine(root, "default_mod.json");
            if (File.Exists(defaultPath))
            {
                var defaultMod = LoadJsonObjectStrict(defaultPath);
                AddJsonFile("default_mod.json", defaultMod);
                AddContainer(defaultMod, null, null, "default_mod.json");
            }

            var metaPath = Path.Combine(root, "meta.json");
            var meta = LoadJsonObjectStrict(metaPath);
            AddJsonFile("meta.json", meta);
            AddContainer(meta["DefaultData"], null, null, "meta.json.DefaultData");
            if (meta["Groups"] is JsonArray groups)
                for (var index = 0; index < groups.Count; ++index)
                    AddGroup(groups[index], $"meta.json.Groups[{index}]");

            foreach (var groupPath in Directory.EnumerateFiles(root, "group_*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetFileName(groupPath);
                AddJsonFile(relative, LoadJsonObjectStrict(groupPath));
                AddGroup(jsonFiles[^1].Value, relative);
            }

            // A mod without any Files mappings cannot be safely normalized: it
            // may use a layout owned by a newer Penumbra schema. Keep it intact.
            if (mappings.Count == 0)
                return new ModCleanupResult([], null);

            var contentByHash = new Dictionary<string, List<(CleanupMapping Mapping, byte[] Bytes)>>(
                StringComparer.OrdinalIgnoreCase);
            var pathHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings)
            {
                var source = SafeContentPath(root, mapping.OldRelativePath);
                if (!File.Exists(source) || !IsSafeModResourceFile(root, source))
                    throw new InvalidDataException($"The referenced content file is missing or unsafe: {mapping.OldRelativePath}");
                var bytes = File.ReadAllBytes(source);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (pathHash.TryGetValue(mapping.CanonicalPath, out var existingHash) &&
                    !string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Different files map to the same canonical path: {mapping.CanonicalPath}");
                pathHash[mapping.CanonicalPath] = hash;
                if (!contentByHash.TryGetValue(hash, out var sameContent))
                    contentByHash[hash] = sameContent = [];
                sameContent.Add((mapping, bytes));
            }

            var keeperByHash = contentByHash.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(item => item.Mapping.CanonicalPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
            var desiredFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var remappedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var content in contentByHash)
            {
                var keeper = keeperByHash[content.Key];
                desiredFiles[keeper] = content.Value[0].Bytes;
                foreach (var item in content.Value)
                {
                    item.Mapping.Files[item.Mapping.GamePath] = keeper;
                    AddPathRemap(remappedPaths, item.Mapping.OldRelativePath, keeper);
                }
            }

            var image = JsonString(meta["Image"]);
            if (!string.IsNullOrWhiteSpace(image))
            {
                if (!TryNormalizeModContentPath(image, out var imagePath))
                    throw new InvalidDataException("The linked mod image path is unsafe.");
                var imagePhysical = SafeContentPath(root, imagePath);
                if (File.Exists(imagePhysical) && IsSafeModResourceFile(root, imagePhysical))
                {
                    var imageBytes = File.ReadAllBytes(imagePhysical);
                    if (desiredFiles.TryGetValue(imagePath, out var mappedBytes) &&
                        !mappedBytes.AsSpan().SequenceEqual(imageBytes))
                        throw new InvalidDataException($"The linked mod image collides with mapped content: {imagePath}");
                    desiredFiles[imagePath] = imageBytes;
                }
            }

            EnsureNoReparsePoints(root);
            var snapshot = CreateCleanupSnapshot(root);
            try
            {
                foreach (var file in desiredFiles)
                    WriteModContentAtomic(root, file.Key, file.Value);
                foreach (var json in jsonFiles)
                    WriteJsonAtomic(Path.Combine(root, json.Path), json.Value);

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray())
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (IsModMetadataFile(relative) || desiredFiles.ContainsKey(relative))
                        continue;
                    File.Delete(file);
                }
                DeleteEmptyDirectories(root);
                TryDeleteCleanupSnapshot(snapshot);
            }
            catch
            {
                RestoreCleanupSnapshot(root, snapshot);
                throw;
            }

            return new ModCleanupResult([], new ModPathRemap(modDirectory, root, remappedPaths));
        }
        catch (Exception e)
        {
            return new ModCleanupResult(
                [$"Mashup cleanup failed; the mashup was retained without normalization or deduplication: {e.Message}"],
                null);
        }
    }

    internal static (IReadOnlyList<string> Warnings, ModPathRemap? PathRemap)
        NormalizeAndDeduplicateModForRegression(string modRoot, string modDirectory)
    {
        var result = NormalizeAndDeduplicateMod(modRoot, modDirectory);
        return (result.Warnings, result.PathRemap);
    }

    private static string? SafeModPathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed is "." or ".." || trimmed.EndsWith(".", StringComparison.Ordinal) ||
            trimmed.EndsWith(" ", StringComparison.Ordinal) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Any(char.IsControl) || trimmed.Contains('/') || trimmed.Contains('\\'))
            throw new InvalidDataException($"The Penumbra group or option name is not a safe path segment: {value}");
        return trimmed;
    }

    private static string BuildCanonicalModPath(string? group, string? option, string gamePath)
    {
        var parts = new List<string> { "Files" };
        if (group is not null)
            parts.Add(group);
        if (option is not null)
            parts.Add(option);
        parts.Add(gamePath);
        return string.Join('/', parts);
    }

    private static bool TryNormalizeModContentPath(string? path, out string normalized)
    {
        normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.Length == 0 || normalized.Length > 4096 || normalized.Contains('\0') ||
            Path.IsPathRooted(normalized))
            return false;
        return normalized.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static string SafeContentPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(path, root) || HasReparsePointInPath(root, path))
            throw new InvalidDataException($"The content path is outside the mod root: {relative}");
        return path;
    }

    private static void AddPathRemap(IDictionary<string, string> paths, string oldPath, string newPath)
    {
        paths[oldPath] = newPath;
        if (oldPath.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            paths[oldPath[6..]] = newPath;
        else
            paths["Files/" + oldPath] = newPath;
    }

    private static bool IsModMetadataFile(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.IndexOf('/') < 0 &&
               (normalized.Equals(OwnershipMarkerFile, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureNoReparsePoints(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The mod contains an unsupported linked path: {entry}");
    }

    private static string CreateCleanupSnapshot(string root)
    {
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidDataException("The mod root has no parent directory.");
        var snapshot = Path.Combine(parent, $".instant-edit-cleanup-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(snapshot);
            CopyDirectoryContents(root, snapshot);
            return snapshot;
        }
        catch
        {
            TryDeleteCleanupSnapshot(snapshot);
            throw;
        }
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The mod contains an unsupported linked path: {entry}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectoryContents(entry, target);
            }
            else
            {
                File.Copy(entry, target, false);
            }
        }
    }

    private static void RestoreCleanupSnapshot(string root, string snapshot)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToArray())
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
            CopyDirectoryContents(snapshot, root);
        }
        finally
        {
            TryDeleteCleanupSnapshot(snapshot);
        }
    }

    private static void TryDeleteCleanupSnapshot(string snapshot)
    {
        try
        {
            if (Directory.Exists(snapshot))
                Directory.Delete(snapshot, true);
        }
        catch
        {
            // A stale snapshot is harmless and can be removed on a later run.
        }
    }

    private static void WriteModContentAtomic(string root, string relative, byte[] bytes)
    {
        if (!TryNormalizeModContentPath(relative, out var normalized))
            throw new InvalidDataException("A normalized content path is unsafe.");
        var target = SafeContentPath(root, normalized);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("A normalized content path has no parent.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".instant-edit-cleanup-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
    }

    internal static string? ValidateExportRequest(string? modName, string? gamePath, string? exportedFile)
    {
        try
        {
            if (!IsSafeModName(modName))
                return "Invalid mod name.";

            if (!IsSafeGamePath(gamePath))
                return "Invalid game path. Expected a safe relative .mdl path.";

            if (string.IsNullOrWhiteSpace(exportedFile) ||
                !string.Equals(Path.GetExtension(exportedFile), ".mdl", StringComparison.OrdinalIgnoreCase))
                return "Exported file must be a .mdl file.";

            var info = new FileInfo(exportedFile);
            if (!info.Exists || info.Length == 0)
                return "Exported .mdl file was not found or is empty.";

            using var file = new FileStream(exportedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = file.ReadByte();
            return null;
        }
        catch (Exception e)
        {
            return $"Exported .mdl file is not readable: {e.Message}";
        }
    }

    internal static bool IsSafeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Length > 1024 ||
            gamePath.Contains('\0') || Path.IsPathRooted(gamePath) || gamePath.Contains('\\') ||
            !string.Equals(Path.GetExtension(gamePath), ".mdl", StringComparison.OrdinalIgnoreCase))
            return false;

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in gamePath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(invalid) >= 0)
                return false;
        }

        return true;
    }

    internal static bool IsSafeGameResourcePath(string? gamePath, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Length > 4096 || gamePath.Contains('\0') ||
            Path.IsPathRooted(gamePath) || gamePath.Contains('\\') ||
            !extensions.Any(extension => gamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return false;
        var invalid = Path.GetInvalidFileNameChars();
        return gamePath.Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..") && segment.IndexOfAny(invalid) < 0);
    }

    internal static bool IsSafeLocalModelPath(string? filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > 4096 || filePath.Contains('\0') ||
                !Path.IsPathRooted(filePath) || filePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(filePath), ".mdl", StringComparison.OrdinalIgnoreCase))
                return false;

            var fullPath = Path.GetFullPath(filePath);
            return filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .All(segment => segment is not ("." or "..")) &&
                !string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeVariantName(string value)
        => PathRules.IsSafeVariantName(value);

    internal static bool IsSafeVariantGroupName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
            return false;
        return value.All(c => !char.IsControl(c));
    }

    private static bool IsSafeRelativeModPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') ||
            Path.IsPathRooted(path) || path.Contains('\\') ||
            !string.Equals(Path.GetExtension(path), ".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static string UniqueMashupGroupName(string modFolder, string requested)
    {
        var names = ReadAllVariantGroups(modFolder)
            .Select(group => JsonString(group["Name"]))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested))
            return requested;
        for (var suffix = 2; suffix < 10000; ++suffix)
        {
            var suffixText = $" ({suffix})";
            var prefix = requested[..Math.Min(requested.Length, 120 - suffixText.Length)].TrimEnd();
            var value = prefix + suffixText;
            if (!names.Contains(value))
                return value;
        }
        throw new InvalidDataException("Could not allocate a unique Penumbra group name.");
    }

    internal static string? WriteMashupGroup(
        string modFolder,
        string name,
        IReadOnlyDictionary<string, string> mappings,
        string? description = null)
    {
        try
        {
            var metaPath = Path.Combine(modFolder, "meta.json");
            var meta = LoadJsonObjectStrict(metaPath);
            var fileVersion = meta["FileVersion"] is JsonValue versionValue &&
                              versionValue.TryGetValue<int>(out var version)
                ? version
                : 3;
            var allGroups = ReadAllVariantGroups(modFolder);
            var priority = allGroups.Select(group => JsonInt(group["Priority"])).DefaultIfEmpty(0).Max();
            if (priority == int.MaxValue)
                return "penumbra_group_priority_exhausted";
            var group = new JsonObject
            {
                ["Version"] = 0,
                ["Type"] = "Single",
                ["Id"] = Guid.NewGuid(),
                ["Name"] = name,
                ["Description"] = description ?? $"Managed by XIV Instant Edit mashup: {name}",
                ["Priority"] = priority + 1,
                ["DefaultSettings"] = 1,
                ["Options"] = new JsonArray
                {
                    new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "None" },
                    new JsonObject
                    {
                        ["Id"] = Guid.NewGuid(),
                        ["Name"] = name,
                        ["Files"] = new JsonObject(mappings.Select(pair =>
                            KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
                    },
                },
            };

            if (fileVersion >= 4)
            {
                var groups = meta["Groups"] as JsonArray ?? new JsonArray();
                meta["Groups"] = groups;
                groups.Add(group);
                meta["LastWrite"] = DateTime.UtcNow;
                WriteJsonAtomic(metaPath, meta);
                return null;
            }

            var highestIndex = Directory.EnumerateFiles(modFolder, "group_*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(file => file is not null && TryReadGroupFileIndex(file, out _))
                .Select(file => { TryReadGroupFileIndex(file!, out var index); return index; })
                .DefaultIfEmpty(0)
                .Max();
            WriteJsonAtomic(Path.Combine(
                modFolder,
                $"group_{highestIndex + 1:D3}_instant_edit_{SanitizeGroupFileName(name)}.json"), group);
            return null;
        }
        catch (Exception e)
        {
            return $"penumbra_group_write_failed: {e.Message}";
        }
    }

    private static void WriteBytesAtomic(string root, string relativePath, byte[] bytes)
    {
        if (!IsSafeRelativeResourcePath(relativePath))
            throw new InvalidDataException("A generated mashup path is unsafe.");
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Mashup file has no parent.");
        if (!IsPathWithin(target, root) || HasReparsePointInPath(root, parent) ||
            (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("A generated mashup destination is unsafe.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void TryDeleteMashupNamespace(string root, string folder)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullFolder) || !IsPathWithin(fullFolder, fullRoot) ||
                string.Equals(fullFolder, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(fullFolder) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(fullFolder, true);
        }
        catch
        {
            // Cleanup is best-effort; the error returned to Blender remains authoritative.
        }
    }

    internal static void ValidateStagedMashupMod(
        string staging,
        string modName,
        IReadOnlyDictionary<string, string> mappings)
    {
        if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The mashup staging directory is unsafe.");
        var meta = LoadJsonObjectStrict(Path.Combine(staging, "meta.json"));
        if (JsonInt(meta["FileVersion"]) != 3 ||
            !string.Equals(JsonString(meta["Name"]), modName, StringComparison.Ordinal))
            throw new InvalidDataException("The mashup mod metadata is invalid.");
        var defaultMod = LoadJsonObjectStrict(Path.Combine(staging, "default_mod.json"));
        if (defaultMod["Files"] is not JsonObject files || files.Count != mappings.Count)
            throw new InvalidDataException("The mashup default mappings are incomplete.");
        foreach (var mapping in mappings)
        {
            if (!string.Equals(JsonString(files[mapping.Key]), mapping.Value, StringComparison.Ordinal) ||
                !IsSafeRelativeResourcePath(mapping.Value))
                throw new InvalidDataException("A mashup default mapping is invalid.");
            var physical = Path.GetFullPath(Path.Combine(
                staging, mapping.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(physical, staging) || !File.Exists(physical) ||
                HasReparsePointInPath(staging, physical))
                throw new InvalidDataException("A staged mashup resource is missing or unsafe.");
        }
    }

    internal static MashupPlanResult BuildMashupPlan(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors)
    {
        if (contributors.Count is < 2 or > 16 ||
            contributors.All(item => !string.Equals(
                item.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal)))
            return MashupPlanFailure("invalid_mashup", "The active Context and at least two contributors are required.");
        if (contributors.Any(item => item.Context.ResourceManifest?.Version != ResourceDependencyManifest.CurrentVersion))
            return MashupPlanFailure("mashup_reimport_required", "Re-import every contributing Context before creating a mashup.");

        var ordered = contributors
            .OrderBy(item => string.Equals(item.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal) ? 0 : 1)
            .ToArray();
        var activeContributor = ordered[0];
        var resolved = new List<(MashupContributor Contributor, string ModelMaterial, MaterialDependency Dependency)>();
        foreach (var contributor in ordered)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var requested in contributor.Materials)
            {
                var normalized = NormalizeModelMaterial(requested);
                if (!seen.Add(normalized))
                    continue;
                var dependency = contributor.Context.ResourceManifest!.Materials.FirstOrDefault(material =>
                    string.Equals(NormalizeModelMaterial(material.ModelMaterial), normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (dependency is null)
                    return MashupPlanFailure("mashup_material_missing",
                        $"Material {requested} was not captured for {contributor.Context.SourceModName}; re-import that Context.");
                resolved.Add((contributor, normalized, dependency));
            }
        }

        var activeMaterials = resolved.Where(item => string.Equals(
            item.Contributor.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal)).ToArray();
        if (activeMaterials.Length == 0)
            return MashupPlanFailure("mashup_material_missing", "The active Context contributes no captured material.");
        var targetDirectories = activeMaterials
            .Select(item => GamePathDirectory(item.Dependency.GamePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetDirectories.Length != 1 || !IsSafeGameResourcePath(
                $"{targetDirectories[0]}/placeholder.mtrl", ".mtrl"))
            return MashupPlanFailure("mashup_material_family_ambiguous",
                "The active materials do not share one safe target material directory.");
        var targetDirectory = targetDirectories[0];

        var canonicalFamilies = activeMaterials
            .Select(item => ParseCanonicalMaterialFamily(Path.GetFileName(NormalizeGamePath(item.Dependency.GamePath))))
            .Where(item => item is not null)
            .Cast<(string Prefix, string Suffix, char Slot)>()
            .ToArray();
        var familyKeys = canonicalFamilies
            .Select(item => $"{item.Prefix}\0{item.Suffix}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string prefix;
        string suffix;
        if (familyKeys.Length == 1)
        {
            prefix = canonicalFamilies[0].Prefix;
            suffix = canonicalFamilies[0].Suffix;
        }
        else if (familyKeys.Length == 0)
        {
            var modelStem = CanonicalTargetModelStem(activeContext.GamePath, targetDirectory);
            if (!Regex.IsMatch(modelStem, @"^[a-z]\d{4}[a-z]\d{4}.*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return MashupPlanFailure("mashup_material_family_ambiguous",
                    "A canonical material family could not be derived from the active model.");
            prefix = $"mt_{modelStem}";
            suffix = CanonicalTargetRaceSuffix(activeContext.GamePath, targetDirectory);
        }
        else
        {
            return MashupPlanFailure("mashup_material_family_ambiguous",
                "The active materials use multiple canonical naming families.");
        }

        var assignments = new List<MashupMaterialAssignment>(resolved.Count);
        var usedSlots = new HashSet<char> { 'a' };
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in activeMaterials)
        {
            var gamePath = NormalizeGamePath(item.Dependency.GamePath);
            var alias = "/" + Path.GetFileName(gamePath);
            var parsed = ParseCanonicalMaterialFamily(Path.GetFileName(gamePath));
            var slot = parsed?.Slot.ToString();
            if (parsed is not null)
                usedSlots.Add(parsed.Value.Slot);
            if (!usedPaths.Add(gamePath))
                return MashupPlanFailure("mashup_material_path_conflict",
                    $"Active materials conflict at {gamePath}.");
            assignments.Add(new MashupMaterialAssignment(
                item.Contributor.Context.ContextId, item.ModelMaterial, alias, gamePath, slot));
        }

        foreach (var item in resolved.Where(item => !string.Equals(
                     item.Contributor.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal)))
        {
            char? allocated = null;
            string? gamePath = null;
            for (var candidate = 'b'; candidate <= 'z'; candidate++)
            {
                if (usedSlots.Contains(candidate))
                    continue;
                var fileName = $"{prefix}_{candidate}{suffix}.mtrl";
                var candidatePath = $"{targetDirectory}/{fileName}";
                if (usedPaths.Contains(candidatePath))
                    continue;
                allocated = candidate;
                gamePath = candidatePath;
                break;
            }
            if (allocated is null || gamePath is null)
                return MashupPlanFailure("mashup_material_slots_exhausted",
                    "No canonical material slots remain between b and z.");
            usedSlots.Add(allocated.Value);
            usedPaths.Add(gamePath);
            assignments.Add(new MashupMaterialAssignment(
                item.Contributor.Context.ContextId,
                item.ModelMaterial,
                "/" + Path.GetFileName(gamePath),
                gamePath,
                allocated.Value.ToString()));
        }

        var fingerprintSource = string.Join("\n", assignments.Select(item =>
            $"{item.ContextId}\0{item.ModelMaterial.ToLowerInvariant()}\0{item.Alias.ToLowerInvariant()}\0{item.GamePath.ToLowerInvariant()}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)))
            .ToLowerInvariant();
        return new MashupPlanResult(true, "mashup_plan_ready", "Mashup material plan is ready.", assignments, fingerprint);
    }

    private static MashupPlanResult MashupPlanFailure(string code, string message)
        => new(false, code, message, Array.Empty<MashupMaterialAssignment>());

    private static (string Prefix, string Suffix, char Slot)? ParseCanonicalMaterialFamily(string fileName)
    {
        var match = Regex.Match(fileName,
            @"^(?<prefix>mt_.+)_(?<slot>[a-z])(?<suffix>_c\d{4})?\.mtrl$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? (match.Groups["prefix"].Value, match.Groups["suffix"].Value,
                char.ToLowerInvariant(match.Groups["slot"].Value[0]))
            : null;
    }

    private static string GamePathDirectory(string value)
    {
        var normalized = NormalizeGamePath(value);
        var separator = normalized.LastIndexOf('/');
        return separator > 0 ? normalized[..separator] : string.Empty;
    }

    private static string CanonicalTargetModelStem(string modelGamePath, string targetMaterialDirectory)
    {
        var stem = Path.GetFileNameWithoutExtension(NormalizeGamePath(modelGamePath));
        var componentMatches = Regex.Matches(
            NormalizeGamePath(targetMaterialDirectory),
            @"(?:^|/)(?<component>[a-uw-z]\d{4})(?=/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in componentMatches)
        {
            var component = match.Groups["component"].Value;
            stem = Regex.Replace(
                stem,
                $@"{Regex.Escape(component[..1])}\d{{4}}",
                component,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        return stem;
    }

    private static string CanonicalTargetRaceSuffix(string modelGamePath, string targetMaterialDirectory)
    {
        var modelStem = Path.GetFileNameWithoutExtension(NormalizeGamePath(modelGamePath));
        var modelRace = Regex.Match(modelStem, @"^c(?<id>\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var targetRace = Regex.Match(NormalizeGamePath(targetMaterialDirectory), @"(?:^|/)c(?<id>\d{4})(?=/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return modelRace.Success && targetRace.Success && !string.Equals(
            modelRace.Groups["id"].Value, targetRace.Groups["id"].Value, StringComparison.OrdinalIgnoreCase)
            ? $"_c{modelRace.Groups["id"].Value}"
            : string.Empty;
    }

    internal static string AllocateMashupTexturePath(
        string activeModelGamePath,
        char slot,
        string usage,
        int textureIndex,
        IReadOnlyDictionary<string, string> mappings)
    {
        var modelPath = NormalizeGamePath(activeModelGamePath);
        var marker = modelPath.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            throw new InvalidDataException("The active model has no canonical texture directory.");
        var directory = modelPath[..marker] + "/texture";
        var stem = Path.GetFileNameWithoutExtension(modelPath);
        var role = usage switch
        {
            "normal" => "n",
            "diffuse" => "d",
            "mask" or "specular" => "s",
            "index" => "id",
            "occlusion" => "o",
            "flow" => "f",
            "decal" => "decal",
            _ => $"t{textureIndex + 1:D2}",
        };
        var baseName = $"{stem}_{slot}_{role}";
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var discriminator = suffix == 1 ? string.Empty : $"_{suffix}";
            var candidate = $"{directory}/{baseName}{discriminator}.tex";
            var dx11Candidate = Dx11TexturePath(candidate, 0x8000);
            if (!mappings.ContainsKey(candidate) && !mappings.ContainsKey(dx11Candidate))
                return candidate;
        }
        throw new InvalidDataException("No canonical texture collision name is available.");
    }

    internal static string RetargetMashupTexturePath(
        string activeModelGamePath,
        string sourceModelGamePath,
        string storedTexturePath)
    {
        var targetModelPath = NormalizeGamePath(activeModelGamePath);
        var sourceModelPath = NormalizeGamePath(sourceModelGamePath);
        var texturePath = NormalizeGamePath(storedTexturePath);
        var modelMarker = targetModelPath.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (modelMarker <= 0)
            throw new InvalidDataException("The active model has no canonical texture directory.");
        if (!IsSafeGameResourcePath(texturePath, ".tex"))
            throw new InvalidDataException("The captured texture path is unsafe.");

        var separator = texturePath.LastIndexOf('/');
        var fileName = separator >= 0 ? texturePath[(separator + 1)..] : texturePath;
        var sourceIdentities = ModelPathIdentities(sourceModelPath);
        var targetIdentities = ModelPathIdentities(targetModelPath);
        var replacements = MatchModelIdentities(sourceIdentities, targetIdentities);

        // Retarget any same-kind identity present in the actual texture name as
        // well. This covers packs that borrow a texture from another item while
        // still keeping the dependency inside the active model's namespace.
        foreach (var targetIdentity in targetIdentities)
            replacements.TryAdd(targetIdentity.Kind, targetIdentity.Value);

        foreach (var source in sourceIdentities)
        {
            if (!replacements.TryGetValue(source.Kind, out var replacement) ||
                string.Equals(source.Value, replacement, StringComparison.OrdinalIgnoreCase))
                continue;
            fileName = ReplaceBoundedModelIdentity(fileName, source.Value, replacement);
        }

        foreach (Match match in Regex.Matches(
                     fileName,
                     @"(?<![a-z])(?<identity>[a-z]\d{4})(?!\d)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var identity = match.Groups["identity"].Value;
            if (replacements.TryGetValue(char.ToLowerInvariant(identity[0]), out var replacement))
                fileName = ReplaceBoundedModelIdentity(fileName, identity, replacement);
        }

        var target = $"{targetModelPath[..modelMarker]}/texture/{fileName}";
        if (!IsSafeGameResourcePath(target, ".tex"))
            throw new InvalidDataException("The retargeted texture path is unsafe.");
        return target;
    }

    internal static string PlanMashupTexturePath(
        string activeModelGamePath,
        string sourceModelGamePath,
        string storedTexturePath,
        string? materialSlot,
        string usage,
        int textureIndex,
        IReadOnlyList<(ushort Flags, string Hash)> textures,
        IReadOnlyDictionary<string, string> textureHashByGamePath,
        IReadOnlyDictionary<string, string> mappings)
    {
        if (textures.Count == 0)
            throw new InvalidDataException("The captured texture group is empty.");

        var target = RetargetMashupTexturePath(activeModelGamePath, sourceModelGamePath, storedTexturePath);
        if (MashupTexturePathConflicts(target, textures, textureHashByGamePath))
        {
            var slot = !string.IsNullOrWhiteSpace(materialSlot) &&
                       materialSlot!.Length == 1 && char.IsAsciiLetterLower(materialSlot[0])
                ? materialSlot[0]
                : 'a';
            target = AllocateMashupTexturePath(
                activeModelGamePath, slot, usage, textureIndex, mappings);
        }

        if (MashupTexturePathConflicts(target, textures, textureHashByGamePath))
            throw new InvalidDataException("Generated texture path still conflicts.");
        return target;
    }

    internal static bool MashupTexturePathConflicts(
        string storedTexturePath,
        IReadOnlyList<(ushort Flags, string Hash)> textures,
        IReadOnlyDictionary<string, string> textureHashByGamePath)
    {
        var proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            var effective = Dx11TexturePath(storedTexturePath, texture.Flags);
            if ((textureHashByGamePath.TryGetValue(effective, out var existingHash) &&
                 !string.Equals(existingHash, texture.Hash, StringComparison.OrdinalIgnoreCase)) ||
                (proposed.TryGetValue(effective, out var proposedHash) &&
                 !string.Equals(proposedHash, texture.Hash, StringComparison.OrdinalIgnoreCase)))
                return true;
            proposed[effective] = texture.Hash;
        }
        return false;
    }

    private static IReadOnlyList<(char Kind, string Value)> ModelPathIdentities(string modelGamePath)
    {
        var marker = modelGamePath.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            return [];
        return Regex.Matches(
                modelGamePath,
                @"(?<![a-z])(?<identity>[a-z]\d{4})(?!\d)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["identity"].Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(identity => (identity[0], identity))
            .ToArray();
    }

    private static Dictionary<char, string> MatchModelIdentities(
        IReadOnlyList<(char Kind, string Value)> source,
        IReadOnlyList<(char Kind, string Value)> target)
    {
        var result = new Dictionary<char, string>();
        foreach (var sourceIdentity in source)
        {
            var sameKind = target.FirstOrDefault(item => item.Kind == sourceIdentity.Kind);
            if (sameKind.Value is not null)
                result[sourceIdentity.Kind] = sameKind.Value;
        }

        var unmatchedSource = source.Where(item => !result.ContainsKey(item.Kind)).ToArray();
        var usedTargetKinds = result.Values
            .Select(value => char.ToLowerInvariant(value[0]))
            .ToHashSet();
        var unmatchedTarget = target.Where(item => !usedTargetKinds.Contains(item.Kind)).ToArray();
        var count = Math.Min(unmatchedSource.Length, unmatchedTarget.Length);
        for (var index = 1; index <= count; ++index)
            result[unmatchedSource[^index].Kind] = unmatchedTarget[^index].Value;
        return result;
    }

    private static string ReplaceBoundedModelIdentity(string value, string source, string target)
        => Regex.Replace(
            value,
            $@"(?<![a-z]){Regex.Escape(source)}(?!\d)",
            target,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    private static string NormalizeModelMaterial(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim(), @"\.\d{3}$", string.Empty);
        if (!normalized.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            normalized += ".mtrl";
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        return normalized;
    }

    internal static string Dx11TexturePath(string path, ushort flags)
        => PathRules.Dx11TexturePath(path, flags);

    private static string ReadNullTerminated(byte[] strings, int offset)
        => PathRules.ReadNullTerminated(strings, offset);

    private static string NormalizeGamePath(string value)
        => PathRules.NormalizeGamePath(value);

    internal static bool IsSafeRelativeModelPath(string? path)
        => path is not null && IsSafeRelativeModPath(path);

    internal static bool IsSafeRelativeResourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') ||
            Path.IsPathRooted(path) || path.Contains('\\'))
            return false;
        var extension = Path.GetExtension(path);
        if (extension is not (".mdl" or ".mtrl" or ".tex") &&
            !extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsSafeModResourceFile(string root, string file)
    {
        try
        {
            var fullPath = Path.GetFullPath(file);
            return IsPathWithin(fullPath, root) &&
                   (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0 &&
                   !HasReparsePointInPath(root, fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static void AddCandidateRoot(List<string> roots, string? path)
    {
        var normalized = NormalizePhysicalPath(path);
        if (normalized is not null && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            roots.Add(normalized);
    }

    private static bool IsPathWithin(string path, string root)
        => PathRules.IsPathWithin(path, root);

    internal static bool IsSafeModName(string? modName)
    {
        if (string.IsNullOrWhiteSpace(modName) || modName is "." or ".." ||
            Path.IsPathRooted(modName) || modName.Contains('/') || modName.Contains('\\') ||
            modName.Length > 128)
            return false;

        return modName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    internal static bool IsSafeNewModName(string? modName)
    {
        if (!IsSafeModName(modName) ||
            !string.Equals(modName, modName!.Trim(), StringComparison.Ordinal) ||
            modName.EndsWith(".", StringComparison.Ordinal) || modName.Any(char.IsControl))
            return false;
        var device = modName.Split('.', 2)[0];
        return !device.Equals("CON", StringComparison.OrdinalIgnoreCase) &&
               !device.Equals("PRN", StringComparison.OrdinalIgnoreCase) &&
               !device.Equals("AUX", StringComparison.OrdinalIgnoreCase) &&
               !device.Equals("NUL", StringComparison.OrdinalIgnoreCase) &&
               !Regex.IsMatch(device, @"^(?:COM|LPT)[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Dictionary<string, HashSet<string>>?[] EmptyResourceResults(int count)
        => Enumerable.Repeat<Dictionary<string, HashSet<string>>?>(null, count).ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}

public sealed record PenumbraMod(string Directory, string Name);

public sealed record PenumbraModResource(
    string GamePath,
    string ActualPath,
    string RelativePath,
    string OptionMapping);

public sealed record PenumbraModSnapshot(
    string Directory,
    string Name,
    string RootPath,
    IReadOnlyList<PenumbraModResource> Resources);
