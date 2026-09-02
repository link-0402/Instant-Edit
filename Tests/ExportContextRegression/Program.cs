using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"[PASS] {message}");
}

static byte[] MinimalMaterial(string texturePath, ushort flags, ushort dataSetSize = 0)
{
    var shader = Encoding.UTF8.GetBytes("character.shpk\0");
    var texture = Encoding.UTF8.GetBytes(texturePath + "\0");
    var strings = shader.Concat(texture).ToArray();
    var bytes = new byte[16 + 4 + strings.Length + dataSetSize + 12];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 0x0103u);
    BitConverter.TryWriteBytes(bytes.AsSpan(4, 2), (ushort)bytes.Length);
    BitConverter.TryWriteBytes(bytes.AsSpan(6, 2), dataSetSize);
    BitConverter.TryWriteBytes(bytes.AsSpan(8, 2), (ushort)strings.Length);
    BitConverter.TryWriteBytes(bytes.AsSpan(10, 2), (ushort)0);
    bytes[12] = 1;
    BitConverter.TryWriteBytes(bytes.AsSpan(16, 2), (ushort)shader.Length);
    BitConverter.TryWriteBytes(bytes.AsSpan(18, 2), flags);
    strings.CopyTo(bytes, 20);
    return bytes;
}

static byte[] MinimalModel(params string[] materials)
{
    var strings = materials.SelectMany(value => Encoding.UTF8.GetBytes(value + "\0")).ToArray();
    var bytes = new byte[68 + 8 + strings.Length];
    BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 0x01000006u);
    BitConverter.TryWriteBytes(bytes.AsSpan(14, 2), checked((ushort)materials.Length));
    BitConverter.TryWriteBytes(bytes.AsSpan(68, 2), checked((ushort)materials.Length));
    BitConverter.TryWriteBytes(bytes.AsSpan(72, 4), checked((uint)strings.Length));
    strings.CopyTo(bytes, 76);
    return bytes;
}

var testRoot = Path.Combine(Path.GetTempPath(), "InstantEditExportContextRegression", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    var originalRoot = Path.Combine(testRoot, "OriginalMod");
    var originalParent = Path.Combine(originalRoot, "Files", "models");
    Directory.CreateDirectory(originalParent);
    var originalTarget = Path.Combine(originalParent, "item.mdl");
    File.WriteAllBytes(originalTarget, [1, 2, 3]);
    const string relative = "Files/models/item.mdl";
    var capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var saved = new PersistedExportContext
    {
        ContextId = "old-context",
        ImportId = "old-import",
        Capability = capability,
        GamePath = "chara/equipment/e0001/model/c0101e0001_top.mdl",
        ObjectIndex = 7,
        TargetFilePath = originalTarget,
        TargetFolder = originalParent,
        SourceModDirectory = "registered-mod",
        SourceModName = "Registered Mod",
        SourceModRootPath = originalRoot,
        TargetRelativePath = relative,
        CallbackPort = 42428,
    };

    IReadOnlyList<PersistedExportContext> persisted = [];
    using var registry = new ExportContextRegistry(
        "current-plugin",
        [saved],
        contexts => persisted = contexts);

    Require(registry.TryReattach(
        saved.ContextId, saved.ImportId, saved.Capability, 42428,
        out var reattached, out var reattachCode),
        "contexts older than 30 days remain authorized");
    Require(reattachCode == "context_reattached" && reattached is not null,
        "reattach returns the authoritative context");
    Require(reattached?.TargetRelativePath == relative,
        "persisted contexts retain their durable target-relative path");
    Require(reattached is { Version: 2, SourceKind: InstantEditImportContext.ModSource,
            DestinationState: InstantEditImportContext.ReadyDestination } &&
            reattached.ResolvedGamePath == saved.GamePath,
        "persisted v1 contexts normalize to ready v2 mod contexts");

    var exportFile = Path.Combine(testRoot, "export.mdl");
    File.WriteAllBytes(exportFile, [4, 5, 6]);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exportFile)));
    Require(registry.TryBeginExport(
        "current-plugin", saved.ContextId, "same-export", capability,
        exportFile, 3, hash, out var first, out _),
        "the first export id is reserved");
    Require(first is { IsOwner: true }, "the first request owns the write");
    Require(registry.TryGetExportStatus(
        "current-plugin", saved.ContextId, "same-export", capability,
        out var pending, out var pendingCode) && pending is { IsCompleted: false } && pendingCode == "export_pending",
        "status lookup reports an in-flight export without repeating it");

    var receipt = new ExportReceipt(true, "export_applied_with_warnings", "written", ["Player-owned redraw warning"], originalTarget);
    registry.CompleteExport(saved.ContextId, "same-export", receipt);
    Require(registry.TryGetExportStatus(
        "current-plugin", saved.ContextId, "same-export", capability,
        out var completed, out var completedCode) && completedCode == "export_complete" &&
        ReferenceEquals(completed!.GetAwaiter().GetResult(), receipt),
        "status lookup recovers the completed receipt");
    Require(registry.TryBeginExport(
        "current-plugin", saved.ContextId, "same-export", capability,
        exportFile, 3, hash, out var duplicate, out var duplicateCode) &&
        duplicate is { IsOwner: false } && duplicateCode == "duplicate_export",
        "a duplicate export id reuses its receipt and cannot perform a second write");
    Require(!registry.TryBeginExport(
        "current-plugin", saved.ContextId, "same-export", capability,
        exportFile, 3, "0" + hash[1..], out _, out var collisionCode) && collisionCode == "duplicate_export_id",
        "an export id cannot be reused for different bytes");

    Require(registry.TryRevoke(saved.ContextId, saved.ImportId, capability, out var revokeCode) &&
        revokeCode == "context_revoked",
        "authenticated context revocation succeeds");
    Require(registry.TryRevoke(saved.ContextId, saved.ImportId, capability, out _),
        "context revocation is idempotent for an already absent context");
    Require(!registry.TryAuthorizeOperation(
        "current-plugin", saved.ContextId, capability, out _, out var revokedCode) && revokedCode == "stale_context",
        "a revoked context cannot authorize another write");
    Require(persisted.Count == 0, "revocation is persisted");

    const string vanillaConsumer = "chara/equipment/e0002/model/c0101e0002_top.mdl";
    const string vanillaResolved = "chara/equipment/e0003/model/c0101e0003_top.mdl";
    var collectionId = Guid.NewGuid();
    var vanillaContext = registry.CreateGameContext(
        vanillaConsumer, vanillaResolved, 7, 42428, collectionId, "Player Collection");
    Require(vanillaContext is
        {
            Version: 2,
            SourceKind: InstantEditImportContext.GameSource,
            DestinationState: InstantEditImportContext.NewModRequiredDestination,
            TargetFilePath: null,
            SourceModDirectory: null,
        } && vanillaContext.GamePath == vanillaConsumer &&
        vanillaContext.ResolvedGamePath == vanillaResolved &&
        vanillaContext.TargetCollectionId == collectionId,
        "vanilla imports retain resolved and consumer paths in a pending v2 context");
    var vanillaModRoot = Path.Combine(testRoot, "Vanilla Edit");
    var vanillaRelative = "Files/" + vanillaConsumer;
    var vanillaTarget = Path.Combine(vanillaModRoot, vanillaRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(vanillaTarget)!);
    File.WriteAllBytes(vanillaTarget, [7, 8, 9]);
    Require(registry.PromoteGameContext(
            vanillaContext.ContextId, "Vanilla Edit", "Vanilla Edit", vanillaModRoot,
            vanillaRelative, out var promotedVanilla) &&
            promotedVanilla is { DestinationState: InstantEditImportContext.ReadyDestination } &&
            promotedVanilla.TargetFilePath == vanillaTarget && persisted.Single().DestinationState ==
            InstantEditImportContext.ReadyDestination,
        "a committed vanilla mod atomically promotes and persists its writable destination");
    using (var reloadedVanillaRegistry = new ExportContextRegistry("reloaded-plugin", persisted))
    {
        Require(reloadedVanillaRegistry.TryReattach(
                vanillaContext.ContextId, vanillaContext.ImportId, vanillaContext.Capability, 42428,
                out var reloadedVanilla, out _) &&
                reloadedVanilla?.TargetFilePath == vanillaTarget &&
                reloadedVanilla.DestinationState == InstantEditImportContext.ReadyDestination,
            "a promoted vanilla context remains writable after plugin restart");
    }
    var malformedGame = PersistedExportContext.FromContext(vanillaContext) with
    {
        ResolvedGamePath = Path.Combine(testRoot, "rooted.mdl"),
    };
    using (var malformedRegistry = new ExportContextRegistry("malformed-plugin", [malformedGame]))
    {
        Require(!malformedRegistry.TryReattach(
                malformedGame.ContextId, malformedGame.ImportId, malformedGame.Capability, 42428,
                out _, out var malformedCode) && malformedCode == "stale_context",
            "malformed v2 game contexts with rooted resolved paths are rejected on restore");
    }

    var movedRoot = Path.Combine(testRoot, "MovedCustomRoot");
    var movedParent = Path.Combine(movedRoot, "Files", "models");
    Directory.CreateDirectory(movedParent);
    var moved = PenumbraService.ResolveSourceModTargetFromRoots(
        "registered-mod", originalTarget, originalRoot, relative, movedRoot, null);
    Require(moved.Target?.FilePath == Path.Combine(movedParent, "item.mdl") && moved.Code == "accepted",
        "a custom or relocated registered root rebases the durable relative target");
    Require(!File.Exists(moved.Target!.FilePath),
        "a missing model file is accepted when its authorized parent still exists");

    var staleRegisteredRoot = Path.Combine(testRoot, "StaleRegisteredRoot");
    var recovered = PenumbraService.ResolveSourceModTargetFromRoots(
        "registered-mod", originalTarget, originalRoot, relative, staleRegisteredRoot, null);
    Require(recovered.Target?.FilePath == originalTarget && recovered.Code == "accepted",
        "a disappeared registered root can recover through the captured authorized root");

    var missingRoot = Path.Combine(testRoot, "MissingParentRoot");
    Directory.CreateDirectory(missingRoot);
    var missing = PenumbraService.ResolveSourceModTargetFromRoots(
        "registered-mod", originalTarget, originalRoot, relative, missingRoot, null);
    Require(missing.Target is null && missing.Code == "destination_missing",
        "a missing authorized parent directory is rejected");

    var fallbackRoot = Path.Combine(testRoot, "FallbackRoot");
    Directory.CreateDirectory(Path.Combine(fallbackRoot, "Files", "models"));
    var ambiguous = PenumbraService.ResolveSourceModTargetFromRoots(
        "registered-mod", originalTarget, originalRoot, relative, null, fallbackRoot);
    Require(ambiguous.Target is null && ambiguous.Code == "destination_ambiguous",
        "multiple non-authoritative roots are rejected as ambiguous");

    var escaped = PenumbraService.ResolveSourceModTargetFromRoots(
        "registered-mod", originalTarget, originalRoot, "../outside.mdl", movedRoot, null);
    Require(escaped.Target is null && escaped.Code == "destination_unsafe",
        "relative path escapes are rejected");

    var reparseRoot = Path.Combine(testRoot, "ReparseRoot");
    var reparseFiles = Path.Combine(reparseRoot, "Files");
    var externalModels = Path.Combine(testRoot, "ExternalModels");
    Directory.CreateDirectory(reparseFiles);
    Directory.CreateDirectory(externalModels);
    try
    {
        Directory.CreateSymbolicLink(Path.Combine(reparseFiles, "models"), externalModels);
        var reparse = PenumbraService.ResolveSourceModTargetFromRoots(
            "registered-mod", originalTarget, originalRoot, relative, reparseRoot, null);
        Require(reparse.Target is null && reparse.Code == "destination_unsafe",
            "reparse-point destinations are rejected");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("[SKIP] reparse-point creation is not permitted on this host");
    }
    catch (PlatformNotSupportedException)
    {
        Console.WriteLine("[SKIP] reparse-point creation is not supported on this host");
    }

    var sourceToStage = Path.Combine(testRoot, "stage-source.mdl");
    var stageBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
    File.WriteAllBytes(sourceToStage, stageBytes);
    var stageHash = Convert.ToHexString(SHA256.HashData(stageBytes));
    var stageResult = await ExportServer.StageExportFileAsync(sourceToStage, stageBytes.Length, stageHash);
    Require(stageResult.Error is null && stageResult.Export is not null,
        "the plugin stages and hashes the submitted model before Penumbra work");
    File.Delete(sourceToStage);
    Require(File.ReadAllBytes(stageResult.Export!.FilePath).SequenceEqual(stageBytes),
        "the staged model remains usable after Blender deletes its source file");
    ExportServer.CleanupStagedExport(stageResult.Export);
    Require(!Directory.Exists(stageResult.Export.DirectoryPath),
        "plugin-owned staging is removed after export completion");

    const string galianModel = @"G:\Penumbra\Galian Hair\chara\human\c0801\obj\hair\h0154\model\c0801h0154_hir.mdl";
    const string effectiveHairPath = "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl";
    var galianResolvedPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        [galianModel] = new(StringComparer.OrdinalIgnoreCase) { effectiveHairPath },
    };
    var galianSource = new ResourceSource(
        ResourceSourceState.LoadedMod,
        "Loaded from: Galian Hair",
        "Galian Hair",
        "Galian Hair",
        @"G:\Penumbra\Galian Hair",
        "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl");
    var supplementedHair = OnScreenService.AddMissingResolvedModels(
        Array.Empty<ResourceNode>(),
        galianResolvedPaths,
        _ => galianSource);
    Require(supplementedHair is [
        {
            ResourceSection: ResourceSection.CharacterFeatures,
            SlotLabel: "Hair",
            GamePath: effectiveHairPath,
            ActualPath: galianModel,
            SourceState: ResourceSourceState.LoadedMod,
            SourceModDirectory: "Galian Hair",
        }
    ], "EST-swapped Galian Hair is restored to the Character features model list");

    var deduplicatedHair = OnScreenService.AddMissingResolvedModels(
        supplementedHair,
        galianResolvedPaths,
        _ => galianSource);
    Require(deduplicatedHair.Count == 1,
        "resource-path reconciliation does not duplicate a model already present in the tree");

    var unattributedHair = OnScreenService.AddMissingResolvedModels(
        Array.Empty<ResourceNode>(),
        galianResolvedPaths,
        path => new ResourceSource(ResourceSourceState.ExternalResolvedFile, "External resolved file", null, null, null, path));
    Require(unattributedHair.Count == 0,
        "resource-path reconciliation does not admit models outside registered Penumbra mods");

    const string vanillaActualModel = "chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl";
    const string vanillaMappedModel = "chara/human/c0201/obj/hair/h0001/model/c0201h0001_hir.mdl";
    var vanillaResolvedPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        [vanillaActualModel] = new(StringComparer.OrdinalIgnoreCase) { vanillaMappedModel },
    };
    var vanillaSource = new ResourceSource(
        ResourceSourceState.GameData, "Game Data", null, null, null, vanillaActualModel);
    var supplementedVanilla = OnScreenService.AddMissingResolvedModels(
        Array.Empty<ResourceNode>(), vanillaResolvedPaths, _ => vanillaSource);
    Require(supplementedVanilla is
        [{ ActualPath: vanillaActualModel, GamePath: vanillaMappedModel,
           SourceState: ResourceSourceState.GameData }],
        "omitted vanilla models retain their resolved and consumer paths with authoritative game-data state");
    Require(OnScreenService.AddMissingResolvedModels(
            supplementedVanilla, vanillaResolvedPaths, _ => vanillaSource).Count == 1,
        "vanilla resource supplementation deduplicates the resolved and consumer path pair");

    var vanillaBranch = new ResourceNode
    {
        Type = "Container", Icon = "", Name = "Vanilla Branch", GamePath = "", ActualPath = "",
        SourceState = ResourceSourceState.SourceUnavailable, SourceLabel = "",
        SlotLabel = "Other", ResourceSection = ResourceSection.Other, SortOrder = 0,
        Children = supplementedVanilla,
    };
    var mixedBranch = vanillaBranch with
    {
        Name = "Mixed Branch",
        Children =
        [
            supplementedVanilla[0],
            supplementedHair[0],
        ],
    };
    var projectedVanilla = OnScreenService.ProjectVisibleResourceNodes([vanillaBranch], false);
    var projectedMixed = OnScreenService.ProjectVisibleResourceNodes([mixedBranch], false);
    Require(projectedVanilla.Count == 0 &&
            OnScreenService.ProjectVisibleResourceNodes([vanillaBranch], true).Count == 1 &&
            projectedMixed is [{ Children: [{ SourceState: ResourceSourceState.LoadedMod }] }] &&
            !projectedMixed.SelectMany(node => node.Children.Prepend(node))
                .Any(node => node.SourceState == ResourceSourceState.GameData),
        "the disabled visibility projection removes vanilla rows while retaining modded descendants");
    Require(OnScreenService.NormalizeActualPath(
            @"chara\human\c0101\obj\hair\h0001\model\c0101h0001_hir.mdl",
            ResourceSourceState.GameData) == vanillaActualModel &&
            PenumbraService.IsSafeGamePath(vanillaActualModel),
        "Penumbra tree game-data paths normalize from FullPath backslashes into editable game paths");

    var manifest = new ResourceDependencyManifest
    {
        Materials =
        [
            new MaterialDependency
            {
                ModelMaterial = "/mt_test.mtrl",
                GamePath = "chara/equipment/e0001/material/v0001/mt_test.mtrl",
                Resource = new SourceResourceLocator
                {
                    Kind = "game",
                    GamePath = "chara/equipment/e0001/material/v0001/mt_test.mtrl",
                    Sha256 = new string('a', 64),
                },
                Textures = [],
            },
        ],
        Manipulations = new JsonArray
        {
            new JsonObject
            {
                ["Type"] = "Eqp",
                ["Manipulation"] = new JsonObject { ["SetId"] = 1, ["Slot"] = "Body", ["Entry"] = 7 },
            },
        },
    };
    IReadOnlyList<PersistedExportContext> manifestPersistence = [];
    using var manifestRegistry = new ExportContextRegistry(
        "manifest-plugin", persist: contexts => manifestPersistence = contexts);
    var manifestContext = manifestRegistry.CreateContext(
        effectiveHairPath, 7, "registered-mod", originalTarget, "Registered Mod", 42428,
        originalRoot, relative, manifest);
    Require(manifestContext.ResourceManifestVersion == 2 &&
            manifestContext.ResourceManifestStatus == "ready" &&
            manifestPersistence.Single().ResourceManifest?.Materials.Count == 1 &&
            manifestPersistence.Single().ResourceManifestStatus == "ready",
        "new contexts persist an exact mashup dependency manifest");
    var sourceCollectionId = Guid.NewGuid();
    var collectionManifestContext = manifestRegistry.CreateContext(
        effectiveHairPath, 7, "registered-mod", originalTarget, "Registered Mod", 42428,
        originalRoot, relative, manifest,
        targetCollectionId: sourceCollectionId,
        targetCollectionName: "Import Snapshot Collection");
    Require(collectionManifestContext.TargetCollectionId == sourceCollectionId &&
            collectionManifestContext.TargetCollectionName == "Import Snapshot Collection" &&
            manifestPersistence.Any(item => item.ContextId == collectionManifestContext.ContextId &&
                item.TargetCollectionId == sourceCollectionId &&
                JsonNode.DeepEquals(item.ResourceManifest?.Manipulations, manifest.Manipulations)),
        "source contexts persist their Penumbra collection identity and manipulation snapshot");
    var newtonsoftConfigurationJson = Newtonsoft.Json.JsonConvert.SerializeObject(manifestPersistence);
    var newtonsoftConfigurationRoundTrip = Newtonsoft.Json.JsonConvert.DeserializeObject<List<PersistedExportContext>>(
        newtonsoftConfigurationJson);
    Require(!newtonsoftConfigurationJson.Contains("\"Parent\"", StringComparison.Ordinal) &&
            JsonNode.DeepEquals(
                newtonsoftConfigurationRoundTrip?[0].ResourceManifest?.Manipulations,
                manifest.Manipulations),
        "Dalamud configuration serialization round-trips manipulation snapshots without JsonNode parent loops");
    var serializedContext = JsonNode.Parse(JsonSerializer.Serialize(manifestContext))!.AsObject();
    Require(serializedContext["sourceGamePath"]?.GetValue<string>() == effectiveHairPath &&
            !serializedContext.ContainsKey("gamePath"),
        "reattach contexts use the synchronized sourceGamePath protocol field");
    var failedManifestContext = manifestRegistry.CreateContext(
        effectiveHairPath, 7, "registered-mod", originalTarget, "Registered Mod", 42428,
        originalRoot, relative, null);
    Require(failedManifestContext.ResourceManifestStatus == "capture_failed",
        "new imports record failed dependency capture explicitly");
    var legacyManifestContext = manifestRegistry.CreateContext(
        effectiveHairPath, 7, "registered-mod", originalTarget, "Registered Mod", 42428,
        originalRoot, relative, manifest with { Version = 1 });
    Require(legacyManifestContext.ResourceManifestStatus == "capture_failed" &&
            legacyManifestContext.ResourceManifest is null,
        "manifest-v1 contexts require re-import before new-mod or mashup export");

    const string swappedMaterial = "chara/human/c0201/obj/hair/h0179/material/v0001/mt_c0201h0179_hir_b_c0801.mtrl";
    var swappedResources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
    {
        [swappedMaterial] = [@"G:\Penumbra\Swapped\mt_c0201h0179_hir_b_c0801.mtrl"],
    };
    var materialWarnings = new List<string>();
    Require(MaterialPreviewBundleBuilder.ResolveMaterialPath(
            "chara/human/c0801/obj/hair/h0179/model/c0801h0179_hir.mdl",
            "/mt_c0101h0179_hir_b_c0801.mtrl",
            swappedResources,
            materialWarnings) == swappedMaterial && materialWarnings.Count == 0,
        "dependency capture resolves an unambiguous race-swapped material mapping");
    swappedResources[
        "chara/human/c0301/obj/hair/h0179/material/v0001/mt_c0301h0179_hir_b_c0801.mtrl"] =
        [@"G:\Penumbra\Swapped\mt_c0301h0179_hir_b_c0801.mtrl"];
    Require(MaterialPreviewBundleBuilder.ResolveMaterialPath(
            "chara/human/c0801/obj/hair/h0179/model/c0801h0179_hir.mdl",
            "/mt_c0101h0179_hir_b_c0801.mtrl",
            swappedResources,
            materialWarnings) is null && materialWarnings.Any(item => item.Contains("racial remapping")),
        "dependency capture rejects ambiguous race-swapped material mappings");
    Require(MaterialPreviewBundleBuilder.ReadModelMaterials(MinimalModel(
            "/mt_c0801h0154_hir_b_c0801.mtrl")).SequenceEqual(
            ["/mt_c0801h0154_hir_b_c0801.mtrl"]),
        "dependency capture reads V6 material paths without Lumina parsing the full model");

    const string optionMaterial = "chara/equipment/e0001/material/v0001/mt_option.mtrl";
    const string defaultTexture = "chara/equipment/e0001/texture/default_d.tex";
    const string unmappedTexture = "chara/equipment/e0001/texture/unmapped_n.tex";
    const string ambiguousTexture = "chara/equipment/e0001/texture/ambiguous_s.tex";
    var browserCandidates = new MaterialResourceCandidate[]
    {
        new(optionMaterial, @"G:\Mods\Test\Files\option-a.mtrl",
            "test-mod", @"G:\Mods\Test", "Files/option-a.mtrl",
            ["meta:group:0:option:0"], "Variant: A"),
        new(optionMaterial, @"G:\Mods\Test\Files\option-b.mtrl",
            "test-mod", @"G:\Mods\Test", "Files/option-b.mtrl",
            ["meta:group:0:option:1"], "Variant: B"),
        new(defaultTexture, @"G:\Mods\Test\Files\default.tex",
            "test-mod", @"G:\Mods\Test", "Files/default.tex",
            ["default"], "Default"),
        new(defaultTexture, @"G:\Mods\Test\Files\other-option.tex",
            "test-mod", @"G:\Mods\Test", "Files/other-option.tex",
            ["meta:group:1:option:1"], "Textures: B"),
        new(unmappedTexture, @"G:\Mods\Test\Files\unmapped.tex",
            "test-mod", @"G:\Mods\Test", "Files/unmapped.tex",
            [], "Unmapped"),
        new(ambiguousTexture, @"G:\Mods\Test\Files\ambiguous-one.tex",
            "test-mod", @"G:\Mods\Test", "Files/ambiguous-one.tex",
            ["meta:group:0:option:0"], "Variant: A"),
        new(ambiguousTexture, @"G:\Mods\Test\Files\ambiguous-two.tex",
            "test-mod", @"G:\Mods\Test", "Files/ambiguous-two.tex",
            ["meta:group:0:option:0"], "Variant: A duplicate"),
    };
    var browserWarnings = new List<string>();
    var scopedBrowserCandidates = MaterialPreviewBundleBuilder.ScopeResourceCandidates(
        browserCandidates, ["meta:group:0:option:0"], browserWarnings);
    Require(scopedBrowserCandidates.Single(candidate => candidate.GamePath == optionMaterial).ActualPath
                .EndsWith("option-a.mtrl", StringComparison.Ordinal) &&
            scopedBrowserCandidates.Single(candidate => candidate.GamePath == defaultTexture).ActualPath
                .EndsWith("default.tex", StringComparison.Ordinal) &&
            scopedBrowserCandidates.Single(candidate => candidate.GamePath == unmappedTexture).ActualPath
                .EndsWith("unmapped.tex", StringComparison.Ordinal),
        "Mod Browser dependency capture prefers the clicked option, then Default, then unmapped resources");
    Require(scopedBrowserCandidates.Count(candidate => candidate.GamePath == ambiguousTexture) == 2 &&
            browserWarnings.Any(item => item.Contains(ambiguousTexture, StringComparison.Ordinal) &&
                                        item.Contains("Variant: A duplicate", StringComparison.Ordinal)),
        "Mod Browser dependency capture reports equal-precedence option ambiguity");
    var effectiveCandidates = MaterialPreviewBundleBuilder.ScopeResourceCandidates(
        browserCandidates.Append(new MaterialResourceCandidate(
            optionMaterial,
            @"G:\Mods\Other\option.mtrl",
            "other-mod",
            @"G:\Mods\Other",
            "Files/option.mtrl"))
            .ToArray(),
        null,
        []);
    Require(effectiveCandidates.Count == browserCandidates.Length + 1 &&
            effectiveCandidates.Any(candidate => candidate.SourceModDirectory == "other-mod"),
        "dependency capture retains effective resources from layered non-participating mods");
    var knownLocator = MaterialPreviewBundleBuilder.CreateKnownSourceLocator(
        optionMaterial,
        scopedBrowserCandidates.Single(candidate => candidate.GamePath == optionMaterial),
        new string('b', 64));
    Require(knownLocator is
            {
                Kind: "mod",
                SourceModDirectory: "test-mod",
                SourceModRootPath: @"G:\Mods\Test",
                SourceRelativePath: "Files/option-a.mtrl",
            },
        "Mod Browser dependency locators preserve the scanned mod source metadata");

    var manifestSourceRoot = Path.Combine(testRoot, "ManifestSource");
    var manifestRelative = "chara/human/c0201/obj/hair/h0154/material/v0001/mt_test.mtrl";
    var manifestSourceFile = Path.Combine(
        manifestSourceRoot, manifestRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(manifestSourceFile)!);
    var manifestSourceBytes = Encoding.UTF8.GetBytes("verified manifest source");
    File.WriteAllBytes(manifestSourceFile, manifestSourceBytes);
    var manifestLocator = new SourceResourceLocator
    {
        Kind = "mod",
        GamePath = "chara/human/c0201/obj/hair/h0154/material/v0001/mt_test.mtrl",
        SourceModDirectory = "registered-mod",
        SourceRelativePath = manifestRelative,
        Sha256 = Convert.ToHexString(SHA256.HashData(manifestSourceBytes)).ToLowerInvariant(),
    };
    var modManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            new MaterialDependency
            {
                ModelMaterial = "/mt_mod.mtrl",
                GamePath = manifestRelative,
                Resource = manifestLocator,
                Textures = [],
            },
        ],
    };
    var modManifestContext = manifestRegistry.CreateContext(
        effectiveHairPath, 7, "registered-mod", originalTarget, "Registered Mod", 42428,
        originalRoot, relative, modManifest);
    var normalizedModelRelative = "Files/normalized/item.mdl";
    var normalizedMaterialRelative = "Files/normalized/mt_test.mtrl";
    Require(manifestRegistry.RemapModPaths(
                "registered-mod",
                originalRoot,
                new Dictionary<string, string>
                {
                    [relative] = normalizedModelRelative,
                    [manifestRelative] = normalizedMaterialRelative,
                }) &&
            manifestRegistry.TryAuthorizeOperation(
                "manifest-plugin", modManifestContext.ContextId, modManifestContext.Capability,
                out var remappedContext, out _) && remappedContext is not null &&
            remappedContext.TargetRelativePath == normalizedModelRelative &&
            remappedContext.TargetFilePath == Path.Combine(originalRoot, "Files", "normalized", "item.mdl") &&
            remappedContext.TargetFolder == Path.Combine(originalRoot, "Files", "normalized") &&
            remappedContext.ResourceManifest!.Materials[0].Resource.SourceRelativePath == normalizedMaterialRelative &&
            manifestPersistence.Any(item => item.ContextId == modManifestContext.ContextId &&
                item.TargetRelativePath == normalizedModelRelative),
        "in-place cleanup remaps context destinations, manifests and persisted locators");
    var verifiedManifestSource = await PenumbraService.ReadVerifiedModManifestResourceAsync(
        manifestLocator, [Path.Combine(testRoot, "WrongManifestRoot"), manifestSourceRoot]);
    Require(verifiedManifestSource?.SequenceEqual(manifestSourceBytes) == true,
        "mashup source verification tries every currently authorized Penumbra root");
    Require(await PenumbraService.ReadVerifiedModManifestResourceAsync(
            manifestLocator with { Sha256 = new string('0', 64) }, [manifestSourceRoot]) is null,
        "mashup source verification still rejects changed source bytes");
    static MaterialDependency CapturedMaterial(string modelMaterial, string gamePath, char hashCharacter) => new()
    {
        ModelMaterial = modelMaterial,
        GamePath = gamePath,
        Resource = new SourceResourceLocator
        {
            Kind = "game",
            GamePath = gamePath,
            Sha256 = new string(hashCharacter, 64),
        },
        Textures = [],
    };
    var galianManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial(
                "/mt_c0801h0154_hir_b_c0801.mtrl",
                "chara/human/c0201/obj/hair/h0154/material/v0001/mt_c0201h0154_hir_b_c0801.mtrl",
                'b'),
        ],
    };
    var bucklerManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/mt_c0101h0179_hir_b_c0801.mtrl", swappedMaterial, 'c'),
            CapturedMaterial(
                "/mt_c0101h0179_hir_c_c0801.mtrl",
                "chara/human/c0201/obj/hair/h0179/material/v0001/mt_c0201h0179_hir_c_c0801.mtrl",
                'd'),
            CapturedMaterial(
                "/mt_c0101h0179_hir_d_c0801.mtrl",
                "chara/human/c0201/obj/hair/h0179/material/v0001/mt_c0201h0179_hir_d_c0801.mtrl",
                'e'),
        ],
    };
    var galianContext = manifestRegistry.CreateContext(
        "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl", 8,
        "galian-mod", originalTarget, "Galian", 42428, originalRoot, relative, galianManifest);
    var bucklerContext = manifestRegistry.CreateContext(
        "chara/human/c0801/obj/hair/h0179/model/c0801h0179_hir.mdl", 9,
        "buckler-mod", originalTarget, "Buckler", 42428, originalRoot, relative, bucklerManifest);
    var canonicalPlan = PenumbraService.BuildMashupPlan(galianContext,
    [
        new MashupContributor(galianContext, ["/mt_c0801h0154_hir_b_c0801.mtrl"]),
        new MashupContributor(bucklerContext,
        [
            "/mt_c0101h0179_hir_b_c0801.mtrl",
            "/mt_c0101h0179_hir_c_c0801.mtrl",
            "/mt_c0101h0179_hir_d_c0801.mtrl",
        ]),
    ]);
    Require(canonicalPlan.Success && canonicalPlan.Assignments.Select(item => item.Alias).SequenceEqual(
        [
            "/mt_c0801h0154_hir_b_c0801.mtrl",
            "/mt_c0201h0154_hir_c_c0801.mtrl",
            "/mt_c0201h0154_hir_d_c0801.mtrl",
            "/mt_c0201h0154_hir_e_c0801.mtrl",
        ]) && canonicalPlan.Assignments.All(item => !item.Alias.Contains("xivie", StringComparison.OrdinalIgnoreCase)),
        "mashup planning preserves the Galian material and allocates Buckler into canonical c, d, and e slots");

    var customTopManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial(
                "/bloodspiller.mtrl",
                "chara/equipment/e0118/material/v0001/bloodspiller.mtrl",
                'f'),
        ],
    };
    var incomingTopManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial(
                "/anything.mtrl",
                "custom/materials/anything.mtrl",
                '1'),
        ],
    };
    var customTopContext = manifestRegistry.CreateContext(
        "chara/equipment/e0118/model/c0201e0118_top.mdl", 10,
        "custom-top", originalTarget, "Custom Top", 42428, originalRoot, relative, customTopManifest);
    var incomingTopContext = manifestRegistry.CreateContext(
        "chara/equipment/e9999/model/c0201e9999_top.mdl", 11,
        "incoming-top", originalTarget, "Incoming Top", 42428, originalRoot, relative, incomingTopManifest);
    var customPlan = PenumbraService.BuildMashupPlan(customTopContext,
    [
        new MashupContributor(customTopContext, ["/bloodspiller.mtrl"]),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ]);
    Require(customPlan.Success &&
            customPlan.Assignments[0].GamePath.EndsWith("/bloodspiller.mtrl", StringComparison.Ordinal) &&
            customPlan.Assignments[1].Alias == "/mt_c0201e0118_top_b.mtrl",
        "custom active materials remain unchanged and incoming gear retains a zero-padded canonical id");
    var singleContextPlan = PenumbraService.BuildMashupPlan(customTopContext,
    [
        new MashupContributor(customTopContext, ["/bloodspiller.mtrl"]),
    ]);
    Require(singleContextPlan.Success && singleContextPlan.Assignments.Count == 1 &&
            singleContextPlan.Assignments[0].Alias == "/bloodspiller.mtrl",
        "single-context plans preserve the captured material for a standalone new mod");
    var mappedAliasManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial(
                "/model-alias.mtrl",
                "chara/equipment/e0118/material/v0001/resolved-target.mtrl",
                '0'),
        ],
    };
    var mappedAliasContext = manifestRegistry.CreateContext(
        "chara/equipment/e0118/model/c0201e0118_top.mdl", 21,
        "mapped-alias", originalTarget, "Mapped Alias", 42428, originalRoot, relative, mappedAliasManifest);
    var mappedAliasPlan = PenumbraService.BuildMashupPlan(mappedAliasContext,
    [
        new MashupContributor(mappedAliasContext, ["/model-alias.mtrl"]),
    ]);
    Require(mappedAliasPlan.Success && mappedAliasPlan.Assignments.Count == 1 &&
            mappedAliasPlan.Assignments[0].Alias == "/model-alias.mtrl" &&
            mappedAliasPlan.Assignments[0].GamePath.EndsWith("/resolved-target.mtrl", StringComparison.Ordinal),
        "active model aliases remain independent from their captured effective material paths");
    var sameSourceContext = incomingTopContext with
    {
        SourceModDirectory = customTopContext.SourceModDirectory,
        SourceModName = customTopContext.SourceModName,
    };
    var sameSourcePlan = PenumbraService.BuildMashupPlan(customTopContext,
    [
        new MashupContributor(customTopContext, ["/bloodspiller.mtrl"]),
        new MashupContributor(sameSourceContext, ["/anything.mtrl"]),
    ]);
    Require(sameSourcePlan.Success && sameSourcePlan.Assignments.Count == 2,
        "multi-context plans accept contributors from the same source mod");

    static MaterialDependency CapturedModMaterial(
        string modelMaterial,
        string gamePath,
        string sourceModDirectory,
        string sourceRelativePath,
        char hashCharacter) => new()
    {
        ModelMaterial = modelMaterial,
        GamePath = gamePath,
        Resource = new SourceResourceLocator
        {
            Kind = InstantEditImportContext.ModSource,
            GamePath = gamePath,
            SourceModDirectory = sourceModDirectory,
            SourceRelativePath = sourceRelativePath,
            Sha256 = new string(hashCharacter, 64),
        },
        Textures = [],
    };

    var sourceBoundaryManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedModMaterial(
                "/owned.mtrl",
                "chara/equipment/e0118/material/v0001/owned.mtrl",
                "custom-top",
                "Files/owned.mtrl",
                '1'),
            CapturedModMaterial(
                "/external.mtrl",
                "chara/human/c0201/obj/body/b0001/material/v0001/external.mtrl",
                "other-mod",
                "Files/external.mtrl",
                '2'),
        ],
    };
    var sourceBoundaryContext = customTopContext with
    {
        ResourceManifest = sourceBoundaryManifest,
    };
    var sourceBoundaryPlan = PenumbraService.BuildMashupPlan(sourceBoundaryContext,
    [
        new MashupContributor(sourceBoundaryContext, ["/owned.mtrl", "/external.mtrl"]),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ]);
    Require(sourceBoundaryPlan.Success &&
            sourceBoundaryPlan.Assignments.Count == 3 &&
            sourceBoundaryPlan.Assignments.Any(item => item.ModelMaterial == "/external.mtrl"),
        "mashup planning assigns pass-through materials attributed to a non-participating source mod");
    var bundledSourceBoundaryPlan = PenumbraService.BuildMashupPlan(sourceBoundaryContext,
    [
        new MashupContributor(sourceBoundaryContext, ["/owned.mtrl", "/external.mtrl"]),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ], bundleExternalDependencies: true);
    Require(bundledSourceBoundaryPlan.Success &&
            bundledSourceBoundaryPlan.Assignments.Select(item => (item.ContextId, item.ModelMaterial, item.Alias, item.GamePath))
                .SequenceEqual(sourceBoundaryPlan.Assignments.Select(item =>
                    (item.ContextId, item.ModelMaterial, item.Alias, item.GamePath))) &&
            bundledSourceBoundaryPlan.Fingerprint != sourceBoundaryPlan.Fingerprint,
        "external bundling preserves material assignments while binding the option into the plan fingerprint");

    var participantDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom-top" };
    var participantLocator = sourceBoundaryManifest.Materials[0].Resource;
    var externalLocator = sourceBoundaryManifest.Materials[1].Resource;
    var gameLocator = externalLocator with
    {
        Kind = InstantEditImportContext.GameSource,
        SourceModDirectory = null,
        SourceRelativePath = null,
    };
    Require(PenumbraService.ShouldBundleMashupDependency(
                participantDirectories, participantLocator, bundleExternalDependencies: false) &&
            PenumbraService.ShouldBundleMashupDependency(
                participantDirectories, participantLocator, bundleExternalDependencies: true) &&
            !PenumbraService.ShouldBundleMashupDependency(
                participantDirectories, externalLocator, bundleExternalDependencies: false) &&
            PenumbraService.ShouldBundleMashupDependency(
                participantDirectories, externalLocator, bundleExternalDependencies: true) &&
            !PenumbraService.ShouldBundleMashupDependency(
                participantDirectories, gameLocator, bundleExternalDependencies: true),
        "mashup dependency classification always bundles participants, optionally bundles external mods, and never bundles game data");
    Require(
        MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial("/mt_c0201b0001_a.mtrl") &&
        MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial("/mt_c0201b0001_bibo.mtrl") &&
        MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial("/mt_c0201b0001_skin.mtrl") &&
        MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial("/mt_c0201b0001_bibopube.mtrl") &&
        MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial("/mt_c0201b0001_piercings.mtrl") &&
        !MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial("/mt_c0201e6106_top_crop.mtrl"),
        "external bundling reuses the established body, skin, pube, and piercing material exclusions");
    var externalGear = CapturedModMaterial(
        "/mt_c0201e6106_top_crop.mtrl",
        "chara/equipment/e6106/material/v0001/mt_c0201e6106_top_crop.mtrl",
        "external-gear",
        "Files/gear.mtrl",
        '8');
    var externalBody = CapturedModMaterial(
        "/mt_c0201b0001_bibo.mtrl",
        "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
        "external-body",
        "Files/body.mtrl",
        '9');
    Require(PenumbraService.CanBundleExternalMashupDependencies(
                externalGear, bundleExternalDependencies: true) &&
            !PenumbraService.CanBundleExternalMashupDependencies(
                externalGear, bundleExternalDependencies: false) &&
            !PenumbraService.CanBundleExternalMashupDependencies(
                externalBody, bundleExternalDependencies: true),
        "external gear dependencies are opt-in while shared body dependency sets remain pass-through");
    var incompletePlan = PenumbraService.BuildMashupPlan(sourceBoundaryContext,
    [
        new MashupContributor(sourceBoundaryContext, ["/missing.mtrl"]),
    ]);
    Require(!incompletePlan.Success && incompletePlan.Code == "mashup_reimport_required",
        "mashup planning rejects incomplete manifests instead of silently omitting requested materials");

    const string bodyMaterialDirectory = "chara/human/c0201/obj/body/b0001/material/v0001";
    const string urgfMaterialDirectory = "chara/equipment/e6106/material/v0001";
    const string microkiniMaterialDirectory = "chara/equipment/e6166/material/v0001";
    var urgfManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/mt_c0201b0001_bibo.mtrl",
                $"{bodyMaterialDirectory}/mt_c0201b0001_bibo.mtrl", '2'),
            CapturedMaterial("/mt_c0201b0001_piercings.mtrl",
                $"{bodyMaterialDirectory}/mt_c0201b0001_piercings.mtrl", '3'),
            CapturedMaterial("/mt_c0201e6106_top_crop.mtrl",
                $"{urgfMaterialDirectory}/mt_c0201e6106_top_crop.mtrl", '4'),
        ],
    };
    var microkiniManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/mt_c0201b0001_bibo.mtrl",
                $"{bodyMaterialDirectory}/mt_c0201b0001_bibo.mtrl", '5'),
            CapturedMaterial("/mt_c0201b0001_bibopube.mtrl",
                $"{bodyMaterialDirectory}/mt_c0201b0001_bibopube.mtrl", '6'),
            CapturedMaterial("/mt_c0201e6166_dwn_wundies.mtrl",
                $"{microkiniMaterialDirectory}/mt_c0201e6166_dwn_wundies.mtrl", '7'),
        ],
    };
    var urgfContext = manifestRegistry.CreateContext(
        "chara/equipment/e6106/model/c0201e6106_top.mdl", 14,
        "urgf-mod", originalTarget, "[lex] URGF", 42428, originalRoot, relative, urgfManifest);
    var microkiniContext = manifestRegistry.CreateContext(
        "chara/equipment/e6166/model/c0201e6166_dwn.mdl", 15,
        "microkini-mod", originalTarget, "Microkini Bottoms", 42428, originalRoot, relative,
        microkiniManifest);
    var urgfActivePlan = PenumbraService.BuildMashupPlan(urgfContext,
    [
        new MashupContributor(urgfContext, urgfManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
        new MashupContributor(microkiniContext,
            microkiniManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
    ]);
    Require(urgfActivePlan.Success &&
            urgfActivePlan.Assignments.Take(3).Select(item => item.GamePath).SequenceEqual(
                urgfManifest.Materials.Select(item => item.GamePath)) &&
            urgfActivePlan.Assignments.Skip(3).Select(item => item.Alias).SequenceEqual(
            [
                "/mt_c0201e6106_top_b.mtrl",
                "/mt_c0201e6106_top_c.mtrl",
                "/mt_c0201e6106_top_d.mtrl",
            ]) &&
            urgfActivePlan.Assignments.Skip(3).All(item =>
                item.GamePath.StartsWith(urgfMaterialDirectory + "/", StringComparison.Ordinal)),
        "URGF preserves body and equipment material paths while Microkini uses the URGF local namespace");
    var microkiniActivePlan = PenumbraService.BuildMashupPlan(microkiniContext,
    [
        new MashupContributor(microkiniContext,
            microkiniManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
        new MashupContributor(urgfContext, urgfManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
    ]);
    Require(microkiniActivePlan.Success &&
            microkiniActivePlan.Assignments.Take(3).Select(item => item.GamePath).SequenceEqual(
                microkiniManifest.Materials.Select(item => item.GamePath)) &&
            microkiniActivePlan.Assignments.Skip(3).Select(item => item.Alias).SequenceEqual(
            [
                "/mt_c0201e6166_dwn_b.mtrl",
                "/mt_c0201e6166_dwn_c.mtrl",
                "/mt_c0201e6166_dwn_d.mtrl",
            ]) &&
            microkiniActivePlan.Assignments.Skip(3).All(item =>
                item.GamePath.StartsWith(microkiniMaterialDirectory + "/", StringComparison.Ordinal)),
        "Microkini preserves body and equipment material paths while URGF uses the Microkini local namespace");
    var urgfSingleContextPlan = PenumbraService.BuildMashupPlan(urgfContext,
    [
        new MashupContributor(urgfContext, urgfManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
    ]);
    Require(urgfSingleContextPlan.Success &&
            urgfSingleContextPlan.Assignments.Select(item => item.GamePath).SequenceEqual(
                urgfManifest.Materials.Select(item => item.GamePath)),
        "single-context Save to new mod preserves active materials spanning multiple directories");

    var versionedManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/local-one.mtrl",
                "chara/equipment/e7777/material/v0001/local-one.mtrl", '8'),
            CapturedMaterial("/local-two.mtrl",
                "chara/equipment/e7777/material/v0002/local-two.mtrl", '9'),
            CapturedMaterial("/local-three.mtrl",
                "chara/equipment/e7777/material/v0002/local-three.mtrl", 'a'),
            CapturedMaterial("/mt_c0201b0001_body.mtrl",
                $"{bodyMaterialDirectory}/mt_c0201b0001_body.mtrl", 'b'),
        ],
    };
    var versionedContext = manifestRegistry.CreateContext(
        "chara/equipment/e7777/model/c0201e7777_top.mdl", 16,
        "versioned-mod", originalTarget, "Versioned", 42428, originalRoot, relative, versionedManifest);
    var versionedPlan = PenumbraService.BuildMashupPlan(versionedContext,
    [
        new MashupContributor(versionedContext,
            versionedManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ]);
    Require(versionedPlan.Success &&
            versionedPlan.Assignments[^1].GamePath ==
            "chara/equipment/e7777/material/v0002/mt_c0201e7777_top_b.mtrl",
        "the most-used model-local material version receives incoming materials");

    var tiedManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/tie-one.mtrl",
                "chara/equipment/e7778/material/v0001/tie-one.mtrl", 'c'),
            CapturedMaterial("/tie-two.mtrl",
                "chara/equipment/e7778/material/v0002/tie-two.mtrl", 'd'),
        ],
    };
    var tiedContext = manifestRegistry.CreateContext(
        "chara/equipment/e7778/model/c0201e7778_top.mdl", 17,
        "tied-mod", originalTarget, "Tied", 42428, originalRoot, relative, tiedManifest);
    var tiedPlan = PenumbraService.BuildMashupPlan(tiedContext,
    [
        new MashupContributor(tiedContext, tiedManifest.Materials.Select(item => item.ModelMaterial).ToArray()),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ]);
    Require(!tiedPlan.Success && tiedPlan.Code == "mashup_target_material_directory_ambiguous",
        "equally represented model-local material versions remain an explicit ambiguity");

    var bodyOnlyManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/mt_c0201b0001_bibo.mtrl",
                $"{bodyMaterialDirectory}/mt_c0201b0001_bibo.mtrl", 'e'),
        ],
    };
    var bodyOnlyContext = manifestRegistry.CreateContext(
        "chara/equipment/e8888/model/c0201e8888_top.mdl", 18,
        "body-only-mod", originalTarget, "Body Only", 42428, originalRoot, relative, bodyOnlyManifest);
    var bodyOnlyPlan = PenumbraService.BuildMashupPlan(bodyOnlyContext,
    [
        new MashupContributor(bodyOnlyContext, ["/mt_c0201b0001_bibo.mtrl"]),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ]);
    Require(bodyOnlyPlan.Success && bodyOnlyPlan.Assignments[^1].GamePath ==
            "chara/equipment/e8888/material/v0001/mt_c0201e8888_top_b.mtrl",
        "models without a local active material derive the standard model-local fallback namespace");

    var duplicateAliasManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/first.mtrl", "custom/one/shared.mtrl", '1'),
            CapturedMaterial("/second.mtrl", "custom/two/shared.mtrl", '2'),
        ],
    };
    var duplicateAliasContext = manifestRegistry.CreateContext(
        "chara/equipment/e8889/model/c0201e8889_top.mdl", 19,
        "duplicate-alias-mod", originalTarget, "Duplicate Alias", 42428, originalRoot, relative,
        duplicateAliasManifest);
    var sameBasenamePlan = PenumbraService.BuildMashupPlan(duplicateAliasContext,
    [
        new MashupContributor(duplicateAliasContext, ["/first.mtrl", "/second.mtrl"]),
    ]);
    Require(sameBasenamePlan.Success &&
            sameBasenamePlan.Assignments.Select(item => item.Alias).SequenceEqual(
                ["/first.mtrl", "/second.mtrl"]),
        "same-basename active materials from different directories retain distinct model aliases");
    var duplicateAliasPlan = PenumbraService.BuildMashupPlan(duplicateAliasContext,
    [
        new MashupContributor(duplicateAliasContext, ["/first.mtrl"]),
        new MashupContributor(duplicateAliasContext, ["/first.mtrl"]),
    ]);
    Require(!duplicateAliasPlan.Success && duplicateAliasPlan.Code == "mashup_material_alias_conflict",
        "duplicate active model aliases remain rejected");

    var duplicatePathManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial("/first.mtrl", "custom/shared.mtrl", '3'),
            CapturedMaterial("/second.mtrl", "custom/shared.mtrl", '4'),
        ],
    };
    var duplicatePathContext = manifestRegistry.CreateContext(
        "chara/equipment/e8890/model/c0201e8890_top.mdl", 20,
        "duplicate-path-mod", originalTarget, "Duplicate Path", 42428, originalRoot, relative,
        duplicatePathManifest);
    var duplicatePathPlan = PenumbraService.BuildMashupPlan(duplicatePathContext,
    [
        new MashupContributor(duplicatePathContext, ["/first.mtrl", "/second.mtrl"]),
    ]);
    Require(!duplicatePathPlan.Success && duplicatePathPlan.Code == "mashup_material_path_conflict",
        "duplicate active virtual material paths remain rejected");

    var materialAddressA = PenumbraService.ContentAddressedMashupMaterialPath(
        "0123456789abcdef", [1, 2, 3]);
    var materialAddressARepeat = PenumbraService.ContentAddressedMashupMaterialPath(
        "0123456789abcdef", [1, 2, 3]);
    var materialAddressB = PenumbraService.ContentAddressedMashupMaterialPath(
        "0123456789abcdef", [3, 2, 1]);
    Require(materialAddressA == materialAddressARepeat && materialAddressA != materialAddressB &&
            materialAddressA.EndsWith(".mtrl", StringComparison.Ordinal),
        "rewritten materials use deterministic content-addressed physical paths independent of source basenames");

    static MaterialDependency ModCoverageMaterial(
        string modelMaterial, string gamePath, string sourceModDirectory, string sourceRelativePath,
        string sha256) => new()
    {
        ModelMaterial = modelMaterial,
        GamePath = gamePath,
        Resource = new SourceResourceLocator
        {
            Kind = InstantEditImportContext.ModSource,
            GamePath = gamePath,
            SourceModDirectory = sourceModDirectory,
            SourceRelativePath = sourceRelativePath,
            Sha256 = sha256,
        },
        Textures = [],
    };

    const string coverageGamePath = "chara/equipment/e0118/material/v0001/mt_coverage.mtrl";
    var coverageOutputPath = Path.Combine(testRoot, "CoverageOutput", "Files", "coverage.mtrl");
    Directory.CreateDirectory(Path.GetDirectoryName(coverageOutputPath)!);
    var coverageBytes = Encoding.UTF8.GetBytes("captured coverage material");
    File.WriteAllBytes(coverageOutputPath, coverageBytes);
    var coverageHash = Convert.ToHexString(SHA256.HashData(coverageBytes));
    const string coverageTextureGamePath = "chara/equipment/e0118/texture/coverage_d.tex";
    var coverageTextureOutputPath = Path.Combine(testRoot, "CoverageOutput", "Files", "coverage.tex");
    var coverageTextureBytes = Encoding.UTF8.GetBytes("captured coverage texture");
    File.WriteAllBytes(coverageTextureOutputPath, coverageTextureBytes);
    var coverageTextureHash = Convert.ToHexString(SHA256.HashData(coverageTextureBytes));
    var coverageMaterial = ModCoverageMaterial(
        "/mt_coverage.mtrl", coverageGamePath, "external-material-mod", "Files/coverage.mtrl", coverageHash) with
    {
        Textures =
        [
            new TextureDependency
            {
                StoredGamePath = coverageTextureGamePath,
                EffectiveGamePath = coverageTextureGamePath,
                Flags = 0,
                Resource = new SourceResourceLocator
                {
                    Kind = InstantEditImportContext.ModSource,
                    GamePath = coverageTextureGamePath,
                    SourceModDirectory = "external-texture-mod",
                    SourceRelativePath = "Files/coverage.tex",
                    Sha256 = coverageTextureHash,
                },
            },
        ],
    };
    var coverageContext = incomingTopContext with
    {
        ResourceManifest = new ResourceDependencyManifest { Materials = [coverageMaterial] },
    };
    var coverageResource = new PenumbraModResource(
        coverageGamePath, coverageOutputPath, "Files/coverage.mtrl", "", []);
    var coverageTextureResource = new PenumbraModResource(
        coverageTextureGamePath, coverageTextureOutputPath, "Files/coverage.tex", "", []);
    var matchingCoverage = await PenumbraService.EvaluateMaterialCoverageAsync(
        customTopContext,
        [new MashupContributor(coverageContext, ["/mt_coverage.mtrl"])],
        [coverageResource, coverageTextureResource]);
    Require(matchingCoverage.Available && matchingCoverage.Covered && matchingCoverage.Missing.Count == 0,
        "material coverage accepts an effective game path with matching captured bytes");
    File.Delete(coverageOutputPath);
    var missingCoverage = await PenumbraService.EvaluateMaterialCoverageAsync(
        customTopContext,
        [new MashupContributor(coverageContext, ["/mt_coverage.mtrl"])],
        []);
    Require(missingCoverage.Available && !missingCoverage.Covered && missingCoverage.Missing.Count == 2 &&
            missingCoverage.Missing.Select(item => item.ResourceType).ToHashSet().SetEquals(["material", "texture"]),
        "material coverage reports required material and texture files absent from the output mod");
    File.WriteAllBytes(coverageOutputPath, Encoding.UTF8.GetBytes("different coverage material"));
    var differingHashCoverage = await PenumbraService.EvaluateMaterialCoverageAsync(
        customTopContext,
        [new MashupContributor(coverageContext, ["/mt_coverage.mtrl"])],
        [coverageResource, coverageTextureResource]);
    Require(differingHashCoverage.Available && !differingHashCoverage.Covered &&
            differingHashCoverage.Missing.Count == 1,
        "material coverage rejects an output material with the same path but different bytes");
    File.WriteAllBytes(coverageOutputPath, coverageBytes);
    File.Delete(coverageTextureOutputPath);
    var missingTextureCoverage = await PenumbraService.EvaluateMaterialCoverageAsync(
        customTopContext,
        [new MashupContributor(coverageContext, ["/mt_coverage.mtrl"])],
        [coverageResource]);
    Require(missingTextureCoverage.Available && !missingTextureCoverage.Covered &&
            missingTextureCoverage.Missing is [{ ResourceType: "texture", GamePath: coverageTextureGamePath }],
        "material coverage detects a texture-only miss even when its parent material is covered");
    var sameModCoverageContext = coverageContext with
    {
        ResourceManifest = new ResourceDependencyManifest
        {
            Materials = [ModCoverageMaterial(
                "/mt_coverage.mtrl", coverageGamePath, customTopContext.SourceModDirectory!,
                "Files/coverage.mtrl", new string('0', 64))],
        },
    };
    var sameModCoverage = await PenumbraService.EvaluateMaterialCoverageAsync(
        customTopContext,
        [new MashupContributor(sameModCoverageContext, ["/mt_coverage.mtrl"])],
        []);
    Require(sameModCoverage.Available && sameModCoverage.Covered,
        "material coverage ignores dependencies sourced from the active output mod");
    var gameDataCoverageContext = coverageContext with
    {
        ResourceManifest = new ResourceDependencyManifest
        {
            Materials =
            [
                new MaterialDependency
                {
                    ModelMaterial = "/mt_game.mtrl",
                    GamePath = coverageGamePath,
                    Resource = new SourceResourceLocator
                    {
                        Kind = InstantEditImportContext.GameSource,
                        GamePath = coverageGamePath,
                        Sha256 = new string('0', 64),
                    },
                    Textures = [],
                },
            ],
        },
    };
    var gameDataCoverage = await PenumbraService.EvaluateMaterialCoverageAsync(
        customTopContext,
        [new MashupContributor(gameDataCoverageContext, ["/mt_game.mtrl"])],
        []);
    Require(gameDataCoverage.Available && gameDataCoverage.Covered,
        "material coverage ignores game-data material dependencies");
    Require(PenumbraService.IsValidMashupContributorCount("new_mod", 1) &&
            !PenumbraService.IsValidMashupContributorCount("active_mod", 1) &&
            PenumbraService.IsValidMashupContributorCount("active_mod", 2),
        "active-mod mashup requests still require at least two contributors");
    var customHairManifest = new ResourceDependencyManifest
    {
        Materials =
        [
            CapturedMaterial(
                "/customhair.mtrl",
                "chara/human/c0201/obj/hair/h0154/material/v0001/customhair.mtrl",
                '2'),
        ],
    };
    var customHairContext = manifestRegistry.CreateContext(
        "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl", 13,
        "custom-hair", originalTarget, "Custom Hair", 42428, originalRoot, relative, customHairManifest);
    var customHairPlan = PenumbraService.BuildMashupPlan(customHairContext,
    [
        new MashupContributor(customHairContext, ["/customhair.mtrl"]),
        new MashupContributor(incomingTopContext, ["/anything.mtrl"]),
    ]);
    Require(customHairPlan.Success &&
            customHairPlan.Assignments[1].Alias == "/mt_c0201h0154_hir_b_c0801.mtrl",
        "custom race-swapped target materials derive canonical components from the captured material directory");

    const string activeMashupHair = "chara/human/c0801/obj/hair/h0004/model/c0801h0004_hir.mdl";
    const string sourceMashupHair = "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl";
    const string sourceMashupTexture =
        "chara/human/c0801/obj/hair/h0154/texture/c0801h0154_hir_b_mask_3098977960_beb563fd.tex";
    const string targetMashupTexture =
        "chara/human/c0801/obj/hair/h0004/texture/c0801h0004_hir_b_mask_3098977960_beb563fd.tex";
    var retargetedHairTexture = PenumbraService.RetargetMashupTexturePath(
        activeMashupHair, sourceMashupHair, sourceMashupTexture);
    Require(retargetedHairTexture == targetMashupTexture,
        "mashup textures retain their suffix while retargeting h0154 to h0004");

    const string sourceGearModel = "chara/equipment/e9999/model/c0201e9999_top.mdl";
    const string targetGearModel = "chara/equipment/e0118/model/c0101e0118_top.mdl";
    Require(PenumbraService.RetargetMashupTexturePath(
            targetGearModel,
            sourceGearModel,
            "custom/source/c0201e9999_top_b_norm_hash.tex") ==
            "chara/equipment/e0118/texture/c0101e0118_top_b_norm_hash.tex",
        "equipment mashup textures retarget race and equipment identities");

    const string sourceWeaponModel = "chara/weapon/w1234/obj/body/b0001/model/c0101w1234b0001.mdl";
    const string targetWeaponModel = "chara/weapon/w5678/obj/body/b0002/model/c0101w5678b0002.mdl";
    Require(PenumbraService.RetargetMashupTexturePath(
            targetWeaponModel,
            sourceWeaponModel,
            "chara/weapon/w1234/obj/body/b0001/texture/c0101w1234b0001_d_hash.tex") ==
            "chara/weapon/w5678/obj/body/b0002/texture/c0101w5678b0002_d_hash.tex",
        "weapon mashup textures retarget every model identity component");

    const string textureHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    var targetDx11Texture = PenumbraService.Dx11TexturePath(targetMashupTexture, 0x8000);
    var existingTargetHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [targetDx11Texture] = textureHash,
    };
    var existingTargetMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [targetDx11Texture] = "Files/existing.tex",
    };
    var reusedTexturePath = PenumbraService.PlanMashupTexturePath(
        activeMashupHair,
        sourceMashupHair,
        sourceMashupTexture,
        "c",
        "normal",
        0,
        [((ushort)0x8000, textureHash)],
        existingTargetHashes,
        existingTargetMappings);
    Require(reusedTexturePath == targetMashupTexture &&
            !PenumbraService.MashupTexturePathConflicts(
                reusedTexturePath, [((ushort)0x8000, textureHash)], existingTargetHashes) &&
            targetDx11Texture.Contains("h0004", StringComparison.Ordinal) &&
            !targetDx11Texture.Contains("h0154", StringComparison.Ordinal),
        "identical DX11 textures reuse the retargeted effective path");

    existingTargetHashes[targetDx11Texture] = new string('b', 64);
    var collisionTexturePath = PenumbraService.PlanMashupTexturePath(
        activeMashupHair,
        sourceMashupHair,
        sourceMashupTexture,
        "c",
        "normal",
        0,
        [((ushort)0x8000, textureHash)],
        existingTargetHashes,
        existingTargetMappings);
    Require(collisionTexturePath ==
            "chara/human/c0801/obj/hair/h0004/texture/c0801h0004_hir_c_n.tex",
        "different texture bytes colliding after retargeting receive a target-family alias");

    var slotlessCollisionPath = PenumbraService.PlanMashupTexturePath(
        activeMashupHair,
        sourceMashupHair,
        sourceMashupTexture,
        null,
        "mask",
        1,
        [((ushort)0x8000, textureHash)],
        existingTargetHashes,
        existingTargetMappings);
    Require(slotlessCollisionPath ==
            "chara/human/c0801/obj/hair/h0004/texture/c0801h0004_hir_a_s.tex",
        "custom preserved materials use slot a for generated texture collision names");

    var retargetedMaterial = PenumbraService.RewriteMaterialTexturePaths(
        MinimalMaterial(sourceMashupTexture, 0x8000, 2176),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceMashupTexture] = targetMashupTexture,
        });
    var retargetedMaterialText = Encoding.UTF8.GetString(retargetedMaterial);
    Require(retargetedMaterialText.Contains(targetMashupTexture, StringComparison.Ordinal) &&
            !retargetedMaterialText.Contains("h0154", StringComparison.Ordinal) &&
            BitConverter.ToUInt16(retargetedMaterial, 18) == 0x8000,
        "MTRL rewriting retains DX11 flags and removes the contributor model identity");

    Require(PenumbraService.AllocateMashupTexturePath(
            galianContext.GamePath, 'c', "normal", 0,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)) ==
            "chara/human/c0801/obj/hair/h0154/texture/c0801h0154_hir_c_n.tex",
        "texture conflicts use the target item stem, assigned material slot, and sampler role");
    var overflowMaterials = Enumerable.Range(0, 26)
        .Select(index => CapturedMaterial(
            $"/custom_{index:D2}.mtrl",
            $"custom/materials/custom_{index:D2}.mtrl",
            'a'))
        .ToArray();
    var overflowManifest = new ResourceDependencyManifest { Materials = overflowMaterials };
    var overflowContext = manifestRegistry.CreateContext(
        "chara/equipment/e9998/model/c0201e9998_top.mdl", 12,
        "overflow-top", originalTarget, "Overflow Top", 42428, originalRoot, relative, overflowManifest);
    var overflowPlan = PenumbraService.BuildMashupPlan(customTopContext,
    [
        new MashupContributor(customTopContext, ["/bloodspiller.mtrl"]),
        new MashupContributor(overflowContext, overflowMaterials.Select(item => item.ModelMaterial).ToArray()),
    ]);
    Require(!overflowPlan.Success && overflowPlan.Code == "mashup_material_slots_exhausted",
        "mashup planning rejects more incoming materials than canonical b through z can hold");

    const string rewrittenTexture = "chara/equipment/e0001/texture/c0101e0001_top_b_n.tex";
    var rewrittenMaterial = PenumbraService.RewriteMaterialTexturePaths(
        MinimalMaterial("a.tex", 0x8000, 2176),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a.tex"] = rewrittenTexture,
        });
    Require(Encoding.UTF8.GetString(rewrittenMaterial).Contains(rewrittenTexture, StringComparison.Ordinal) &&
            BitConverter.ToUInt16(rewrittenMaterial, 18) == 0x8000,
        "Dawntrail MTRL rewriting grows short string tables and preserves DX11 texture flags");
    var rewrittenLegacyMaterial = PenumbraService.RewriteMaterialTexturePaths(
        MinimalMaterial("legacy.tex", 0, 512),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["legacy.tex"] = rewrittenTexture,
        });
    Require(Encoding.UTF8.GetString(rewrittenLegacyMaterial).Contains(rewrittenTexture, StringComparison.Ordinal),
        "legacy MTRL rewriting reparses after string-table growth");

    var mashupMappings = new Dictionary<string, string>
    {
        [effectiveHairPath] = "Files/xiv-instant-edit/mashups/test/model.mdl",
        ["chara/equipment/e0001/material/v0001/mt_test.mtrl"] =
            "Files/xiv-instant-edit/mashups/test/materials/mt_test.mtrl",
    };
    var mashupFileSwaps = new Dictionary<string, string>
    {
        ["chara/equipment/e0001/material/v0001/mt_external.mtrl"] =
            "chara/equipment/e9999/material/v0001/mt_external.mtrl",
    };
    var markerlessMashupRoot = Path.Combine(testRoot, "MarkerlessMashup");
    var markerlessMapping = new Dictionary<string, string>
    {
        [effectiveHairPath] = "Files/xiv-instant-edit/mashups/test/model.mdl",
    };
    Directory.CreateDirectory(Path.Combine(markerlessMashupRoot, "Files", "xiv-instant-edit", "mashups", "test"));
    File.WriteAllBytes(
        Path.Combine(markerlessMashupRoot, "Files", "xiv-instant-edit", "mashups", "test", "model.mdl"),
        [1, 2, 3]);
    File.WriteAllText(Path.Combine(markerlessMashupRoot, "meta.json"),
        "{\"FileVersion\":3,\"Name\":\"Markerless Mashup\"}");
    File.WriteAllText(Path.Combine(markerlessMashupRoot, "default_mod.json"), new JsonObject
    {
        ["Version"] = 0,
        ["Files"] = new JsonObject { [effectiveHairPath] = markerlessMapping[effectiveHairPath] },
        ["FileSwaps"] = new JsonObject(mashupFileSwaps.Select(pair =>
            KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
    }.ToJsonString());
    var markerlessValidation = true;
    try
    {
        PenumbraService.ValidateStagedMashupMod(
            markerlessMashupRoot, "Markerless Mashup", markerlessMapping,
            expectedFileSwaps: mashupFileSwaps);
    }
    catch
    {
        markerlessValidation = false;
    }
    Require(markerlessValidation &&
            !File.Exists(Path.Combine(markerlessMashupRoot, ".instant-edit-owner.json")),
        "new mashup staging validates without creating an ownership marker");

    var vanillaStageRoot = Path.Combine(testRoot, "VanillaStage");
    Directory.CreateDirectory(vanillaStageRoot);
    var stagedVanillaRelative = PenumbraService.StageGameModelMod(
        vanillaStageRoot, "Vanilla Model Edit", vanillaConsumer, [10, 11, 12]);
    var stagedVanillaMeta = JsonNode.Parse(
        File.ReadAllText(Path.Combine(vanillaStageRoot, "meta.json")))!.AsObject();
    var stagedVanillaDefault = JsonNode.Parse(
        File.ReadAllText(Path.Combine(vanillaStageRoot, "default_mod.json")))!.AsObject();
    Require(stagedVanillaRelative == "Files/" + vanillaConsumer &&
            stagedVanillaMeta["FileVersion"]!.GetValue<int>() == 3 &&
            stagedVanillaMeta["Author"]!.GetValue<string>() == "XIV Instant Edit" &&
            stagedVanillaDefault["Files"]!.AsObject().Count == 1 &&
            stagedVanillaDefault["Files"]![vanillaConsumer]!.GetValue<string>() == stagedVanillaRelative &&
            stagedVanillaDefault["Manipulations"]!.AsArray().Count == 0 &&
            File.ReadAllBytes(Path.Combine(
                vanillaStageRoot, stagedVanillaRelative.Replace('/', Path.DirectorySeparatorChar)))
                .SequenceEqual(new byte[] { 10, 11, 12 }) &&
            !File.Exists(Path.Combine(vanillaStageRoot, ".instant-edit-owner.json")) &&
            Directory.GetFiles(vanillaStageRoot, "*.mtrl", SearchOption.AllDirectories).Length == 0 &&
            Directory.GetFiles(vanillaStageRoot, "*.tex", SearchOption.AllDirectories).Length == 0,
        "first vanilla export stages one v3 model mapping without ownership or copied dependencies");
    var unsafeVanillaStageRejected = false;
    try
    {
        PenumbraService.StageGameModelMod(vanillaStageRoot, "Unsafe Edit", "../outside.mdl", [1]);
    }
    catch (InvalidDataException)
    {
        unsafeVanillaStageRejected = true;
    }
    Require(unsafeVanillaStageRejected,
        "vanilla mod staging rejects consumer-path traversal before writing files");

    var manipulationV3Root = Path.Combine(testRoot, "ManipulationsV3");
    Directory.CreateDirectory(manipulationV3Root);
    File.WriteAllText(Path.Combine(manipulationV3Root, "meta.json"),
        "{\"FileVersion\":3,\"Name\":\"Manipulations V3\"}");
    File.WriteAllText(Path.Combine(manipulationV3Root, "default_mod.json"), new JsonObject
    {
        ["Manipulations"] = new JsonArray
        {
            new JsonObject
            {
                ["Type"] = "Eqp",
                ["Manipulation"] = new JsonObject { ["SetId"] = 1, ["Slot"] = "Body", ["Entry"] = 99 },
            },
            new JsonObject { ["Type"] = "Atr", ["Entry"] = "default" },
        },
    }.ToJsonString());
    File.WriteAllText(Path.Combine(manipulationV3Root, "group_001_low.json"), new JsonObject
    {
        ["Type"] = "Multi",
        ["Name"] = "Details",
        ["Priority"] = 2,
        ["Options"] = new JsonArray
        {
            new JsonObject
            {
                ["Name"] = "Enabled Detail",
                ["Manipulations"] = new JsonArray
                {
                    new JsonObject { ["Type"] = "Eqdp", ["Entry"] = "multi-1" },
                    new JsonObject { ["Type"] = "Imc", ["Entry"] = "multi-2" },
                },
            },
            new JsonObject
            {
                ["Name"] = "Disabled Detail",
                ["Manipulations"] = new JsonArray { new JsonObject { ["Type"] = "Imc", ["Entry"] = "disabled" } },
            },
        },
    }.ToJsonString());
    File.WriteAllText(Path.Combine(manipulationV3Root, "group_002_high.json"), new JsonObject
    {
        ["Type"] = "Single",
        ["Name"] = "Shape",
        ["Priority"] = 9,
        ["Options"] = new JsonArray
        {
            new JsonObject
            {
                ["Name"] = "Enabled Shape",
                ["Manipulations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Type"] = "Eqp",
                        ["Manipulation"] = new JsonObject { ["SetId"] = 1, ["Slot"] = "Body", ["Entry"] = 11 },
                    },
                    new JsonObject { ["Type"] = "Est", ["Entry"] = "high-2" },
                },
            },
        },
    }.ToJsonString());
    var v3Manipulations = PenumbraService.CaptureEffectiveManipulations(
        manipulationV3Root,
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Details"] = ["Enabled Detail"],
            ["Shape"] = ["Enabled Shape"],
        });
    Require(v3Manipulations.Select(item => item!["Type"]!.GetValue<string>())
                .SequenceEqual(["Eqp", "Est", "Eqdp", "Imc", "Atr"]) &&
            v3Manipulations.All(item => item!["Entry"]?.GetValue<string>() != "disabled") &&
            v3Manipulations[0]!["Manipulation"]!["Entry"]!.GetValue<int>() == 11,
        "v3 manipulation capture applies selected options before Default with unique first-wins conflicts");

    var manipulationV4Root = Path.Combine(testRoot, "ManipulationsV4");
    Directory.CreateDirectory(manipulationV4Root);
    File.WriteAllText(Path.Combine(manipulationV4Root, "meta.json"), new JsonObject
    {
        ["FileVersion"] = 4,
        ["Name"] = "Manipulations V4",
        ["DefaultData"] = new JsonObject
        {
            ["Manipulations"] = new JsonArray { new JsonObject { ["Type"] = "Atr", ["Entry"] = "v4-default" } },
        },
        ["Groups"] = new JsonArray
        {
            new JsonObject
            {
                ["Type"] = "Single",
                ["Name"] = "Variant",
                ["Priority"] = 4,
                ["Options"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Name"] = "Enabled",
                        ["Manipulations"] = new JsonArray { new JsonObject { ["Type"] = "Eqp", ["Entry"] = "v4-option" } },
                    },
                },
            },
        },
    }.ToJsonString());
    var v4Manipulations = PenumbraService.CaptureEffectiveManipulations(
        manipulationV4Root,
        new Dictionary<string, IReadOnlyList<string>> { ["Variant"] = ["Enabled"] });
    Require(v4Manipulations.Select(item => item!["Entry"]!.GetValue<string>())
                .SequenceEqual(["v4-option", "v4-default"]),
        "v4 manipulation capture merges enabled embedded options before DefaultData");
    var copiedDefault = PenumbraService.CreateMashupDefaultData(
        mashupMappings, v3Manipulations, mashupFileSwaps);
    Require(JsonNode.DeepEquals(copiedDefault["Manipulations"], v3Manipulations) &&
            copiedDefault["FileSwaps"]!.AsObject().Count == mashupFileSwaps.Count,
        "new-mod default data preserves active manipulations and pass-through FileSwaps");

    var v3Root = Path.Combine(testRoot, "MashupV3");
    Directory.CreateDirectory(v3Root);
    File.WriteAllText(Path.Combine(v3Root, "meta.json"), "{\"FileVersion\":3,\"Name\":\"Keep Me\"}");
    const string preservedV3 = "{\"Version\":0,\"Type\":\"Single\",\"Name\":\"Existing\",\"Priority\":2,\"Options\":[]}";
    File.WriteAllText(Path.Combine(v3Root, "group_001_existing.json"), preservedV3);
    Require(PenumbraService.WriteMashupGroup(
                v3Root, "Mashup", mashupMappings, manipulations: v3Manipulations,
                fileSwaps: mashupFileSwaps) is null &&
            File.ReadAllText(Path.Combine(v3Root, "group_001_existing.json")) == preservedV3,
        "v3 mashup group creation preserves unrelated group metadata");
    var createdV3 = JsonNode.Parse(File.ReadAllText(
        Directory.GetFiles(v3Root, "group_002_*.json").Single()))!.AsObject();
    Require(createdV3["DefaultSettings"]!.GetValue<int>() == 1 &&
            createdV3["Options"]!.AsArray()[1]!["Files"]!.AsObject().Count == mashupMappings.Count &&
            createdV3["Options"]!.AsArray()[1]!["FileSwaps"]!.AsObject().Count == mashupFileSwaps.Count &&
            JsonNode.DeepEquals(createdV3["Options"]!.AsArray()[1]!["Manipulations"], v3Manipulations),
        "v3 mashup group enables its option and attaches manipulations and FileSwaps");

    var v4Root = Path.Combine(testRoot, "MashupV4");
    Directory.CreateDirectory(v4Root);
    File.WriteAllText(Path.Combine(v4Root, "meta.json"),
        "{\"FileVersion\":4,\"Name\":\"Keep Me\",\"Custom\":17," +
        "\"Groups\":[{\"Version\":0,\"Type\":\"Single\",\"Id\":\"b8297758-0ef7-4ca0-8b9d-c08421c1ab2c\",\"Name\":\"Existing\",\"Priority\":3,\"Options\":[]}]}" );
    Require(PenumbraService.WriteMashupGroup(
            v4Root, "Mashup", mashupMappings, fileSwaps: mashupFileSwaps) is null,
        "v4 mashup group creation succeeds");
    var updatedV4 = JsonNode.Parse(File.ReadAllText(Path.Combine(v4Root, "meta.json")))!.AsObject();
    Require(updatedV4["Custom"]!.GetValue<int>() == 17 && updatedV4["Groups"]!.AsArray().Count == 2 &&
            updatedV4["Groups"]!.AsArray()[1]!["Options"]!.AsArray()[1]!["Files"]!.AsObject().Count ==
            mashupMappings.Count &&
            updatedV4["Groups"]!.AsArray()[1]!["Options"]!.AsArray()[1]!["FileSwaps"]!.AsObject().Count ==
            mashupFileSwaps.Count,
        "v4 mashup group creation preserves metadata, files, and FileSwaps");

    var cleanupV3Root = Path.Combine(testRoot, "CleanupV3");
    Directory.CreateDirectory(Path.Combine(cleanupV3Root, "legacy"));
    var cleanupModelPath = "chara/equipment/e0001/model/c0101e0001_top.mdl";
    var cleanupMaterialPath = "chara/equipment/e0001/material/v0001/mt_test.mtrl";
    var duplicateBytes = new byte[] { 9, 8, 7 };
    File.WriteAllBytes(Path.Combine(cleanupV3Root, "legacy", "first.mdl"), duplicateBytes);
    File.WriteAllBytes(Path.Combine(cleanupV3Root, "legacy", "second.mdl"), duplicateBytes);
    File.WriteAllBytes(Path.Combine(cleanupV3Root, "legacy", "material.mtrl"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(cleanupV3Root, "legacy", "unused.tex"), [4, 5, 6]);
    File.WriteAllBytes(Path.Combine(cleanupV3Root, "preview.png"), [7, 7, 7]);
    File.WriteAllText(Path.Combine(cleanupV3Root, "meta.json"), new JsonObject
    {
        ["FileVersion"] = 3,
        ["Name"] = "Cleanup V3",
        ["Description"] = "Keep this description",
        ["Image"] = "preview.png",
        ["Custom"] = 41,
    }.ToJsonString());
    File.WriteAllText(Path.Combine(cleanupV3Root, "default_mod.json"), new JsonObject
    {
        ["Version"] = 0,
        ["Files"] = new JsonObject { [cleanupModelPath] = "legacy/first.mdl" },
        ["FileSwaps"] = new JsonObject { ["old.path"] = "new.path" },
        ["Manipulations"] = new JsonArray { new JsonObject { ["Type"] = "Keep" } },
    }.ToJsonString());
    File.WriteAllText(Path.Combine(cleanupV3Root, "group_001_existing.json"), new JsonObject
    {
        ["Version"] = 0,
        ["Type"] = "Single",
        ["Name"] = "Existing",
        ["Custom"] = 73,
        ["Options"] = new JsonArray
        {
            new JsonObject { ["Name"] = "None" },
            new JsonObject
            {
                ["Name"] = "Option",
                ["Files"] = new JsonObject
                {
                    [cleanupModelPath] = "legacy/second.mdl",
                    [cleanupMaterialPath] = "legacy/material.mtrl",
                },
            },
        },
    }.ToJsonString());
    var cleanupV3 = PenumbraService.NormalizeAndDeduplicateModForRegression(cleanupV3Root, "cleanup-v3");
    var cleanupV3Default = JsonNode.Parse(File.ReadAllText(Path.Combine(cleanupV3Root, "default_mod.json")))!.AsObject();
    var cleanupV3Group = JsonNode.Parse(File.ReadAllText(Path.Combine(cleanupV3Root, "group_001_existing.json")))!.AsObject();
    var cleanupV3ModelPhysical = Path.Combine(cleanupV3Root, "Files", cleanupModelPath.Replace('/', Path.DirectorySeparatorChar));
    Require(cleanupV3.Warnings.Count == 0 && cleanupV3.PathRemap is not null &&
            cleanupV3Default["Files"]![cleanupModelPath]!.GetValue<string>() == "Files/" + cleanupModelPath &&
            cleanupV3Group["Options"]![1]!["Files"]![cleanupModelPath]!.GetValue<string>() == "Files/" + cleanupModelPath &&
            File.Exists(cleanupV3ModelPhysical) && !File.Exists(Path.Combine(cleanupV3Root, "legacy", "first.mdl")) &&
            !File.Exists(Path.Combine(cleanupV3Root, "legacy", "second.mdl")) &&
            !File.Exists(Path.Combine(cleanupV3Root, "legacy", "unused.tex")),
        "v3 cleanup normalizes mappings, removes duplicates and unreferenced content");
    var cleanupV3Meta = JsonNode.Parse(File.ReadAllText(Path.Combine(cleanupV3Root, "meta.json")))!.AsObject();
    Require(cleanupV3Meta["Description"]!.GetValue<string>() == "Keep this description" &&
            cleanupV3Meta["Custom"]!.GetValue<int>() == 41 && File.Exists(Path.Combine(cleanupV3Root, "preview.png")) &&
            cleanupV3Default["FileSwaps"] is not null && cleanupV3Default["Manipulations"] is not null &&
            cleanupV3Group["Custom"]!.GetValue<int>() == 73,
        "v3 cleanup preserves metadata, file swaps, manipulations and linked images");

    var cleanupV4Root = Path.Combine(testRoot, "CleanupV4");
    Directory.CreateDirectory(Path.Combine(cleanupV4Root, "legacy"));
    var cleanupTexturePath = "chara/equipment/e0001/texture/c0101e0001_top.tex";
    File.WriteAllBytes(Path.Combine(cleanupV4Root, "legacy", "first.tex"), [5, 4, 3]);
    File.WriteAllBytes(Path.Combine(cleanupV4Root, "legacy", "second.tex"), [5, 4, 3]);
    File.WriteAllBytes(Path.Combine(cleanupV4Root, "legacy", "unused.mtrl"), [2, 2, 2]);
    File.WriteAllText(Path.Combine(cleanupV4Root, "meta.json"), new JsonObject
    {
        ["FileVersion"] = 4,
        ["Name"] = "Cleanup V4",
        ["Custom"] = 84,
        ["DefaultData"] = new JsonObject
        {
            ["Files"] = new JsonObject { [cleanupTexturePath] = "legacy/first.tex" },
        },
        ["Groups"] = new JsonArray
        {
            new JsonObject
            {
                ["Name"] = "Group",
                ["Options"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Name"] = "Option",
                        ["Files"] = new JsonObject { [cleanupTexturePath] = "legacy/second.tex" },
                        ["FileSwaps"] = new JsonObject { ["a"] = "b" },
                    },
                },
            },
        },
    }.ToJsonString());
    var cleanupV4 = PenumbraService.NormalizeAndDeduplicateModForRegression(cleanupV4Root, "cleanup-v4");
    var cleanupV4Meta = JsonNode.Parse(File.ReadAllText(Path.Combine(cleanupV4Root, "meta.json")))!.AsObject();
    Require(cleanupV4.Warnings.Count == 0 && cleanupV4.PathRemap is not null &&
            cleanupV4Meta["Custom"]!.GetValue<int>() == 84 &&
            cleanupV4Meta["DefaultData"]!["Files"]![cleanupTexturePath]!.GetValue<string>() == "Files/" + cleanupTexturePath &&
            cleanupV4Meta["Groups"]![0]!["Options"]![0]!["Files"]![cleanupTexturePath]!.GetValue<string>() == "Files/" + cleanupTexturePath &&
            cleanupV4Meta["Groups"]![0]!["Options"]![0]!["FileSwaps"] is not null &&
            !File.Exists(Path.Combine(cleanupV4Root, "legacy", "unused.mtrl")),
        "v4 cleanup normalizes embedded groups and preserves group metadata");

    var cleanupFailureRoot = Path.Combine(testRoot, "CleanupFailure");
    Directory.CreateDirectory(cleanupFailureRoot);
    File.WriteAllText(Path.Combine(cleanupFailureRoot, "meta.json"), "{\"FileVersion\":3,\"Name\":\"Cleanup Failure\"}");
    File.WriteAllText(Path.Combine(cleanupFailureRoot, "default_mod.json"),
        "{\"Version\":0,\"Files\":{\"chara/equipment/e0001/model/c0101e0001_top.mdl\":\"missing/item.mdl\"}}");
    var cleanupFailure = PenumbraService.NormalizeAndDeduplicateModForRegression(cleanupFailureRoot, "cleanup-failure");
    Require(cleanupFailure.Warnings.Count == 1 && cleanupFailure.PathRemap is null &&
            JsonNode.Parse(File.ReadAllText(Path.Combine(cleanupFailureRoot, "default_mod.json")))!["Files"]![cleanupModelPath]!.GetValue<string>() ==
                "missing/item.mdl",
        "cleanup failures warn and retain the committed mashup unchanged");

    var sourceDescription = PenumbraService.FormatMashupDescription(
    [
        new MashupContributor(galianContext with { SourceModName = "Galian Hair" }, []),
        new MashupContributor(bucklerContext with { SourceModName = "[178] Buckler (Swapped)" }, []),
    ]);
    Require(sourceDescription == "Mashup created by XIV Instant Edit from \"Galian Hair\" and \"[178] Buckler (Swapped)\".",
        "mashup descriptions list source mods in contributor order");
    var externalDescription = PenumbraService.FormatMashupDescription(
        [new MashupContributor(galianContext with { SourceModName = "Galian Hair" }, [])],
        ["External Skin", "External Hair"]);
    Require(externalDescription.EndsWith(
            "Requires external mods: External Skin, External Hair.", StringComparison.Ordinal),
        "mashup descriptions list required external mods without blocking export");
    var describedGroupRoot = Path.Combine(testRoot, "DescribedGroup");
    Directory.CreateDirectory(describedGroupRoot);
    File.WriteAllText(Path.Combine(describedGroupRoot, "meta.json"), "{\"FileVersion\":3,\"Name\":\"Described\"}");
    Require(PenumbraService.WriteMashupGroup(describedGroupRoot, "Mashup", mashupMappings, sourceDescription) is null &&
            JsonNode.Parse(File.ReadAllText(Directory.GetFiles(describedGroupRoot, "group_*.json").Single()))!["Description"]!.GetValue<string>() ==
                sourceDescription &&
            JsonNode.Parse(File.ReadAllText(Path.Combine(describedGroupRoot, "meta.json")))!["Name"]!.GetValue<string>() ==
                "Described",
        "in-place mashups put the source description on their new group without rewriting parent metadata");

    Require(PenumbraService.IsSafeNewModName("My Mashup") &&
            !PenumbraService.IsSafeNewModName("My Mashup.") &&
            !PenumbraService.IsSafeNewModName("CON") &&
            !PenumbraService.IsSafeNewModName("../My Mashup"),
        "new mashup mod names reject unsafe Windows destinations");

    Require(manifestRegistry.TryBeginExport(
            "manifest-plugin", manifestContext.ContextId, "fingerprint-export", manifestContext.Capability,
            exportFile, 3, hash, out var fingerprintOwner, out _, "fingerprint-a") &&
            fingerprintOwner is { IsOwner: true },
        "mashup request fingerprints reserve an export");
    Require(manifestRegistry.TryBeginExport(
            "manifest-plugin", manifestContext.ContextId, "fingerprint-export", manifestContext.Capability,
            exportFile, 3, hash, out var fingerprintDuplicate, out _, "fingerprint-a") &&
            fingerprintDuplicate is { IsOwner: false },
        "an exact mashup retry reuses its receipt");
    Require(!manifestRegistry.TryBeginExport(
            "manifest-plugin", manifestContext.ContextId, "fingerprint-export", manifestContext.Capability,
            exportFile, 3, hash, out _, out var fingerprintCollision, "fingerprint-b") &&
            fingerprintCollision == "duplicate_export_id",
        "changed mashup metadata cannot reuse an export id");
    manifestRegistry.CompleteExport(manifestContext.ContextId, "fingerprint-export",
        new ExportReceipt(true, "complete", "complete"));

    Console.WriteLine("All export-context regressions passed.");
}
finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, true);
}
