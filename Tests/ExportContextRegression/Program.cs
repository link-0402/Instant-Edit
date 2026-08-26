using System.Security.Cryptography;
using InstantEdit.Models;
using InstantEdit.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"[PASS] {message}");
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
        ModName = "registered-mod",
        TargetFilePath = originalTarget,
        TargetFolder = originalParent,
        SourceModDirectory = "registered-mod",
        SourceModName = "Registered Mod",
        SourceModRootPath = originalRoot,
        TargetRelativePath = null,
        CallbackPort = 42428,
        ExpiresAt = DateTimeOffset.UtcNow.AddYears(-2),
    };

    IReadOnlyList<PersistedExportContext> persisted = [];
    using var registry = new ExportContextRegistry(
        "current-plugin",
        [saved],
        contexts => persisted = contexts,
        _ => new ActorIdentity { ObjectIndex = 7, Address = 999 },
        TimeSpan.FromDays(30));

    Require(registry.TryReattach(
        saved.ContextId, saved.ImportId, saved.Capability, 42428,
        out var reattached, out var reattachCode),
        "contexts older than 30 days remain authorized");
    Require(reattachCode == "context_reattached" && reattached is not null,
        "reattach returns the authoritative context");
    Require(reattached!.ActorIdentity is null,
        "reattach never binds the context to the current object-table occupant");
    Require(reattached.TargetRelativePath == relative,
        "legacy contexts derive their durable target-relative path");

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

    Console.WriteLine("All export-context regressions passed.");
}
finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, true);
}
