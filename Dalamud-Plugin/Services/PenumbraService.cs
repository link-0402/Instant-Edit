using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

/// <summary> Result of applying an export to Penumbra. </summary>
public sealed record ExportResult(bool Success, string Message);

/// <summary>
/// Wraps all Penumbra IPC used by Instant Edit:
/// reading the resolved model files of on-screen game objects and
/// writing/updating a persistent mod so the edited model applies in-game.
/// </summary>
public sealed class PenumbraService
{
    private sealed record SourceModTarget(string Directory, string Folder, string FilePath);

    private const string OwnershipMarkerFile = ".instant-edit-owner.json";
    private const string OwnershipSchema = "instant-edit.owner";
    private const string OwnershipOwner = "Luci";
    private const string VariantGroupDescriptionPrefix = "Managed by Instant Edit variant group: ";

    private readonly IDalamudPluginInterface  _pi;
    private readonly GetGameObjectResourcePaths _getPaths;
    private readonly GetPlayerResourceTrees _getPlayerTrees;
    private readonly GetModDirectory           _getModDirectory;
    private readonly GetModList                 _getModList;
    private readonly AddMod                     _addMod;
    private readonly ReloadMod                  _reloadMod;
    private readonly GetCollectionForObject      _getCollectionForObject;
    private readonly TrySetMod                   _trySetMod;
    private readonly TrySetModPriority            _trySetModPriority;
    private readonly RedrawObject               _redrawObject;
    private readonly RedrawAll                  _redrawAll;
    private readonly IFramework                  _framework;
    private readonly IPluginLog                 _log;
    private readonly IObjectTable?              _objects;
    private readonly SemaphoreSlim              _exportGate = new(1, 1);

    public PenumbraService(
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log,
        IObjectTable? objects = null)
    {
        _pi               = pi;
        _log             = log;
        _framework       = framework;
        _objects         = objects;
        _getPaths        = new GetGameObjectResourcePaths(pi);
        _getPlayerTrees  = new GetPlayerResourceTrees(pi);
        _getModDirectory = new GetModDirectory(pi);
        _getModList      = new GetModList(pi);
        _addMod          = new AddMod(pi);
        _reloadMod       = new ReloadMod(pi);
        _getCollectionForObject = new GetCollectionForObject(pi);
        _trySetMod       = new TrySetMod(pi);
        _trySetModPriority = new TrySetModPriority(pi);
        _redrawObject    = new RedrawObject(pi);
        _redrawAll       = new RedrawAll(pi);
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
    /// Write the exported mdl into the persistent Instant Edit mod and reload + redraw so it applies in-game.
    /// </summary>
    /// <param name="modName">Directory name of the mod used for exports.</param>
    /// <param name="gamePath">The game path the model was imported from (e.g. chara/weapon/.../x.mdl).</param>
    /// <param name="exportedFile">The mdl file produced by Blender.</param>
    /// <param name="objectIndex">Index of the game object the model came from.</param>
    public ExportResult ApplyExport(string modName, string gamePath, string exportedFile, int objectIndex)
        => ApplyExportAsync(modName, gamePath, exportedFile, objectIndex).GetAwaiter().GetResult();

    /// <summary>
    /// Applies an export without blocking the caller while Penumbra IPC is marshalled
    /// onto the framework thread. The synchronous overload remains for old callers.
    /// </summary>
    public Task<ExportResult> ApplyExportAsync(
        string modName,
        string gamePath,
        string exportedFile,
        int objectIndex)
        => ApplyExportAsync(modName, gamePath, exportedFile, objectIndex, null);

    public Task<ExportResult> ApplyExportAsync(
        string modName,
        string gamePath,
        string exportedFile,
        int objectIndex,
        ActorIdentity? actorIdentity)
        => ApplyExportAsync(modName, gamePath, exportedFile, objectIndex, actorIdentity, gamePath);

    /// <summary>
    /// Writes to <paramref name="gamePath"/> after revalidating the actor against
    /// <paramref name="sourceGamePath"/>. This permits a server-derived sibling
    /// variant without allowing the client to choose an arbitrary validation path.
    /// </summary>
    public async Task<ExportResult> ApplyExportAsync(
        string modName,
        string gamePath,
        string exportedFile,
        int objectIndex,
        ActorIdentity? actorIdentity,
        string sourceGamePath,
        string? penumbraVariantName = null,
        string? penumbraVariantGroupName = null)
    {
        if (objectIndex is < 0 or > ushort.MaxValue)
            return new ExportResult(false, "Invalid object index.");

        var validationError = ValidateExportRequest(modName, gamePath, exportedFile);
        if (validationError is not null)
            return new ExportResult(false, validationError);
        if (!IsSafeGamePath(sourceGamePath))
            return new ExportResult(false, "Invalid source game path.");
        var resolvedVariantGroupName = penumbraVariantGroupName ?? "Instant Edit Variants";
        if (penumbraVariantName is not null &&
            (!IsSafeVariantName(penumbraVariantName) || !IsSafeVariantGroupName(resolvedVariantGroupName)))
            return new ExportResult(false, "Invalid Penumbra variant or option group name.");

        await _exportGate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Even read-only Penumbra IPC is kept on the framework thread. This
            // also makes a disconnected/reloading Penumbra a normal failed result.
            var targetError = await _framework.RunOnFrameworkThread(
                () => ValidateTargetOnFramework(sourceGamePath, objectIndex, actorIdentity)).ConfigureAwait(false);
            if (targetError is not null)
                return new ExportResult(false, targetError);

            var root = await _framework.RunOnFrameworkThread(GetModDirectory).ConfigureAwait(false);
            if (root is null)
                return new ExportResult(false, "Couldn't retrieve the Penumbra mod directory.");

            var modFolder = Path.Combine(root, modName);
            var ownershipError = EnsureOwnedModFolder(modFolder, modName);
            if (ownershipError is not null)
                return new ExportResult(false, ownershipError);
            var writeError = WriteModel(modFolder, gamePath, exportedFile);
            if (writeError is not null)
                return new ExportResult(false, writeError);

            if (penumbraVariantName is not null)
            {
                var groupError = WriteVariantGroup(
                    modFolder,
                    sourceGamePath,
                    "Files/" + gamePath,
                    penumbraVariantName,
                    resolvedVariantGroupName);
                if (groupError is not null)
                    return new ExportResult(false, groupError);
            }

            var modState = await _framework.RunOnFrameworkThread(
                () => GetModStateOnFramework(modName)).ConfigureAwait(false);
            if (!modState.Success)
                return new ExportResult(false, "Could not retrieve the Penumbra mod list.");

            if (modState.Exists)
            {
                var reloadError = await _framework.RunOnFrameworkThread(
                    () => ReloadModOnFramework(modName)).ConfigureAwait(false);
                if (reloadError is not null)
                    return reloadError;
            }
            else
            {
                var addError = await AddNewModAsync(modName).ConfigureAwait(false);
                if (addError is not null)
                    return addError;
            }

            // The add event is awaited outside the framework callback. Only after
            // it fires do we re-enter the framework to configure and redraw.
            return await _framework.RunOnFrameworkThread(
                () => ConfigureModOnFramework(modName, objectIndex)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to apply export to Penumbra.");
            return new ExportResult(false, $"Failed to apply export: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>
    /// Write an export back to the resolved file in its original Penumbra mod.
    /// The destination was captured in the server-owned import context and is
    /// revalidated against Penumbra's current mod list before every write.
    /// </summary>
    public async Task<ExportResult> ApplySourceExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string sourceGamePath,
        string exportedFile,
        int objectIndex,
        ActorIdentity? actorIdentity,
        string? variantName,
        string? variantGroupName,
        bool setupVariantInPenumbra,
        ExportRedrawMode redrawMode = ExportRedrawMode.Self)
    {
        if (objectIndex is < 0 or > ushort.MaxValue)
            return new ExportResult(false, "Invalid object index.");
        if (!IsSafeModName(sourceModDirectory) || !IsSafeGamePath(sourceGamePath) ||
            !IsSafeLocalModelPath(sourceFilePath))
            return new ExportResult(false, "The original Penumbra model destination is invalid.");
        var validationError = ValidateExportRequest(sourceModDirectory, sourceGamePath, exportedFile);
        if (validationError is not null)
            return new ExportResult(false, validationError);
        if (variantName is not null && !IsSafeVariantName(variantName))
            return new ExportResult(false, "Invalid variant name.");
        if (variantName is not null && string.Equals(
                variantName,
                Path.GetFileNameWithoutExtension(sourceGamePath),
                StringComparison.OrdinalIgnoreCase))
            return new ExportResult(false, "Variant name must differ from the originally imported model name.");
        if (setupVariantInPenumbra && variantName is null)
            return new ExportResult(false, "Penumbra variant setup requires Save as Variant.");
        if (setupVariantInPenumbra && !IsSafeVariantGroupName(variantGroupName))
            return new ExportResult(false, "Penumbra variant setup requires an option group name.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var actorError = await _framework.RunOnFrameworkThread(
                () => ValidateTargetOnFramework(sourceGamePath, objectIndex, actorIdentity, sourceFilePath)).ConfigureAwait(false);
            if (actorError is not null)
                return new ExportResult(false, actorError);

            var resolved = await _framework.RunOnFrameworkThread(
                () => ResolveSourceModTargetOnFramework(sourceModDirectory, sourceFilePath)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new ExportResult(false, resolved.Error ?? "The original Penumbra mod is no longer available.");

            var targetFile = variantName is null
                ? resolved.Target.FilePath
                : Path.Combine(Path.GetDirectoryName(resolved.Target.FilePath)!, variantName + ".mdl");
            var writeError = WriteModelToOriginalLocation(
                resolved.Target.Folder,
                targetFile,
                exportedFile);
            if (writeError is not null)
                return new ExportResult(false, writeError);

            if (setupVariantInPenumbra)
            {
                var relativeVariantPath = Path.GetRelativePath(resolved.Target.Folder, targetFile).Replace('\\', '/');
                var groupError = WriteVariantGroup(
                    resolved.Target.Folder,
                    sourceGamePath,
                    relativeVariantPath,
                    variantName!,
                    variantGroupName!);
                if (groupError is not null)
                    return new ExportResult(false, groupError);
            }

            var reloadError = await _framework.RunOnFrameworkThread(
                () => ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                return reloadError;

            return await _framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    switch (redrawMode)
                    {
                        case ExportRedrawMode.All:
                            _redrawAll.Invoke();
                            break;
                        case ExportRedrawMode.Glamourer:
                            return new ExportResult(
                                true,
                                $"Exported to {targetFile} and reloaded {resolved.Target.Directory}; redraw left to Glamourer.");
                        default:
                            _redrawObject.Invoke(objectIndex);
                            break;
                    }

                    var redrawTarget = redrawMode == ExportRedrawMode.All ? "all actors" : "the source actor";
                    return new ExportResult(
                        true,
                        $"Exported to {targetFile}, reloaded {resolved.Target.Directory}, and redrew {redrawTarget}.");
                }
                catch (Exception e)
                {
                    _log.Error(e, "Penumbra redraw failed after exporting to the source mod.");
                    return new ExportResult(false, $"Export succeeded, but redraw failed: {e.Message}");
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to export to the original Penumbra mod.");
            return new ExportResult(false, $"Failed to export to the original mod: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private (SourceModTarget? Target, string? Error) ResolveSourceModTargetOnFramework(
        string sourceModDirectory,
        string sourceFilePath)
    {
        var root = GetModDirectory();
        if (string.IsNullOrWhiteSpace(root))
            return (null, "Couldn't retrieve the Penumbra mod directory.");
        if (!TryGetModList(out var modList))
            return (null, "Could not retrieve the Penumbra mod list.");

        var registeredDirectory = modList.Keys.FirstOrDefault(directory =>
            string.Equals(directory, sourceModDirectory, StringComparison.OrdinalIgnoreCase));
        if (registeredDirectory is null)
            return (null, "The source mod is no longer registered in Penumbra.");

        try
        {
            var modRoot = Path.GetFullPath(root);
            var modFolder = Path.GetFullPath(Path.Combine(modRoot, registeredDirectory));
            var modelFile = Path.GetFullPath(sourceFilePath);
            if (!IsPathWithin(modFolder, modRoot) || !IsPathWithin(modelFile, modFolder) ||
                !File.Exists(modelFile) || !IsSafeLocalModelPath(modelFile))
                return (null, "The original model is no longer a valid file inside its Penumbra mod.");
            if (HasReparsePointInPath(modFolder, Path.GetDirectoryName(modelFile)!) ||
                (File.GetAttributes(modelFile) & FileAttributes.ReparsePoint) != 0)
                return (null, "The original model path contains an unsupported reparse point.");

            return (new SourceModTarget(registeredDirectory, modFolder, modelFile), null);
        }
        catch (Exception e)
        {
            return (null, $"Could not validate the original model destination: {e.Message}");
        }
    }

    private static string? WriteModelToOriginalLocation(
        string modFolder,
        string targetFile,
        string exportedFile)
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

    private (bool Success, bool Exists) GetModStateOnFramework(string modName)
    {
        if (!TryGetModList(out var modList))
            return (false, false);

        return (true, modList.ContainsKey(modName));
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
            _log.Error(e, "Penumbra failed while reloading the Instant Edit mod.");
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
            _log.Error(e, "Penumbra failed while adding the Instant Edit mod.");
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

    private ExportResult ConfigureModOnFramework(string modName, int objectIndex)
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
            _log.Error(e, "Could not enable the Instant Edit mod in Penumbra.");
            return new ExportResult(false, $"Penumbra enable failed: {e.Message}");
        }

        if (enabledResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected enabling the mod ({enabledResult}).");

        PenumbraApiEc priorityResult;
        try
        {
            // Penumbra's documented ModPriority.MaxValue is int.MaxValue.
            priorityResult = _trySetModPriority.Invoke(
                collection.EffectiveCollection.Id,
                modName,
                int.MaxValue,
                modName);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not prioritize the Instant Edit mod in Penumbra.");
            return new ExportResult(false, $"Penumbra priority failed: {e.Message}");
        }

        if (priorityResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected the mod priority ({priorityResult}).");

        try
        {
            _redrawObject.Invoke(objectIndex);
            return new ExportResult(true, $"Applied {modName} to {collection.EffectiveCollection.Name} ({objectIndex}).");
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


    private string? ValidateTargetOnFramework(
        string gamePath,
        int objectIndex,
        ActorIdentity? expectedActor,
        string? expectedResolvedPath = null)
    {
        if (_objects is null || expectedActor is null)
            return null;

        try
        {
            if (expectedActor.ObjectIndex != objectIndex)
                return "target_actor_changed";

            var current = _objects[(ushort)objectIndex];
            if (current is null || current.Address == nint.Zero || current.Address.ToInt64() != expectedActor.Address)
                return "target_actor_changed";

            var paths = _getPaths.Invoke((ushort)objectIndex);
            if (paths is null || paths.Length == 0 ||
                !paths.Any(dictionary => dictionary is not null && dictionary.Any(resource =>
                    (expectedResolvedPath is null || PathsEqual(resource.Key, expectedResolvedPath)) &&
                    resource.Value.Any(path => string.Equals(path, gamePath, StringComparison.OrdinalIgnoreCase)))))
                return "target_path_changed";
        }
        catch (Exception e)
        {
            _log.Debug($"Could not revalidate export target: {e.Message}");
            return "target_revalidation_failed";
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? EnsureOwnedModFolder(string modFolder, string modName)
    {
        if (Directory.Exists(modFolder))
        {
            try
            {
                if ((File.GetAttributes(modFolder) & FileAttributes.ReparsePoint) != 0)
                    return "destination_mod_unsafe";
            }
            catch (Exception e)
            {
                return $"destination_mod_unreadable: {e.Message}";
            }
        }
        else
        {
            Directory.CreateDirectory(modFolder);
        }

        var markerPath = Path.Combine(modFolder, OwnershipMarkerFile);
        if (File.Exists(markerPath))
        {
            try
            {
                var marker = LoadJsonObject(markerPath);
                if (!string.Equals(marker["schema"]?.GetValue<string>(), OwnershipSchema, StringComparison.Ordinal) ||
                    !string.Equals(marker["owner"]?.GetValue<string>(), OwnershipOwner, StringComparison.Ordinal) ||
                    !string.Equals(marker["modName"]?.GetValue<string>(), modName, StringComparison.Ordinal))
                    return "destination_mod_not_owned";
            }
            catch
            {
                return "destination_mod_not_owned";
            }
        }
        else if (DirectoryHasLegacyOwnership(modFolder, modName))
        {
            // Existing releases did not write a marker. Migrate only the exact
            // metadata signature that this plugin itself used.
            WriteOwnershipMarker(markerPath, modName);
        }
        else if (Directory.EnumerateFileSystemEntries(modFolder).Any())
        {
            return "destination_mod_not_owned";
        }
        else
        {
            WriteOwnershipMarker(markerPath, modName);
        }

        var metaPath = Path.Combine(modFolder, "meta.json");
        var meta = LoadJsonObject(metaPath);
        meta["FileVersion"] = 3;
        meta["Name"] = modName;
        meta["Author"] = "Luci";
        meta["Description"] = "Managed by the Instant Edit plugin.";
        meta["Image"] = "";
        meta["Version"] = "";
        meta["Website"] = "";
        meta["ModTags"] = new JsonArray();
        WriteJsonAtomic(metaPath, meta);

        var defaultPath = Path.Combine(modFolder, "default_mod.json");
        var defaultMod = LoadDefaultMod(defaultPath);
        defaultMod["Version"] ??= 0;
        defaultMod["Files"] ??= new JsonObject();
        defaultMod["FileSwaps"] ??= new JsonObject();
        // Older Instant Edit versions wrote an object here. It is not a valid
        // Penumbra container, and there is no safe object-to-manipulation mapping.
        if (defaultMod["Manipulations"] is not JsonArray)
            defaultMod["Manipulations"] = new JsonArray();
        WriteJsonAtomic(defaultPath, defaultMod);

        return null;
    }

    private static bool DirectoryHasLegacyOwnership(string modFolder, string modName)
    {
        try
        {
            var meta = LoadJsonObject(Path.Combine(modFolder, "meta.json"));
            return string.Equals(meta["Name"]?.GetValue<string>(), modName, StringComparison.Ordinal) &&
                   string.Equals(meta["Author"]?.GetValue<string>(), OwnershipOwner, StringComparison.Ordinal) &&
                   string.Equals(meta["Description"]?.GetValue<string>(), "Managed by the Instant Edit plugin.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteOwnershipMarker(string path, string modName)
    {
        var marker = new JsonObject
        {
            ["schema"] = OwnershipSchema,
            ["version"] = 1,
            ["owner"] = OwnershipOwner,
            ["modName"] = modName,
        };
        WriteJsonAtomic(path, marker);
    }

    private static string? WriteModel(string modFolder, string gamePath, string exportedFile)
    {
        var filesRoot = Path.Combine(modFolder, "Files");
        var relativePath = "Files/" + gamePath;
        var target       = Path.Combine(modFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (HasReparsePointInPath(modFolder, Path.GetDirectoryName(target)!) ||
            (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            return "destination_mod_unsafe";

        Directory.CreateDirectory(filesRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(exportedFile, target, true);

        var defaultPath = Path.Combine(modFolder, "default_mod.json");
        var defaultMod = LoadDefaultMod(defaultPath);
        if (!defaultMod.ContainsKey("Version"))
            defaultMod["Version"] = 0;

        var files = defaultMod["Files"] as JsonObject ?? new JsonObject();
        files[gamePath] = relativePath;
        defaultMod["Files"] = files;

        if (defaultMod["FileSwaps"] is not (JsonObject or null))
            defaultMod["FileSwaps"] = new JsonObject();
        if (defaultMod["Manipulations"] is not JsonArray)
            defaultMod["Manipulations"] = new JsonArray();
        WriteJsonAtomic(defaultPath, defaultMod);
        return null;
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
        var noneOption = options
            .OfType<JsonObject>()
            .FirstOrDefault(option => string.Equals(JsonString(option["Name"]), "None", StringComparison.Ordinal));
        if (noneOption is null)
        {
            noneOption = new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = "None",
            };
            options.Insert(0, noneOption);
        }

        var variantOption = options
            .OfType<JsonObject>()
            .FirstOrDefault(option =>
                !ReferenceEquals(option, noneOption) &&
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
            variantOption["Files"] = new JsonObject
            {
                [sourceGamePath] = relativeVariantPath,
            };
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
           GroupTargetsGamePath(group, sourceGamePath);

    private static bool GroupTargetsGamePath(JsonObject group, string sourceGamePath)
    {
        if (group["Options"] is not JsonArray options)
            return false;

        foreach (var option in options.OfType<JsonObject>())
        {
            if (option["Files"] is not JsonObject files)
                continue;

            foreach (var file in files)
            {
                if (!SameGamePath(file.Key, sourceGamePath))
                    return false;
            }
        }

        return true;
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
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value is "." or ".." ||
            value.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            return false;
        return value.All(c => !char.IsControl(c));
    }

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

    private static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeModName(string? modName)
    {
        if (string.IsNullOrWhiteSpace(modName) || modName is "." or ".." ||
            Path.IsPathRooted(modName) || modName.Contains('/') || modName.Contains('\\') ||
            modName.Length > 128)
            return false;

        return modName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static Dictionary<string, HashSet<string>>?[] EmptyResourceResults(int count)
        => Enumerable.Repeat<Dictionary<string, HashSet<string>>?>(null, count).ToArray();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
