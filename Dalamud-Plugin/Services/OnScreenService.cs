using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Helpers;

namespace InstantEdit.Services;

/// <summary>
/// Captures the local player's and their currently spawned owned-object Penumbra resource
/// trees. Penumbra selects the candidate indices; missing owned-object trees are recovered
/// explicitly, then each is joined to the object table and admitted by supported object-kind
/// checks before its immutable, pruned snapshot is published.
/// </summary>
public sealed class OnScreenService
{
    private readonly IObjectTable _objects;
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly PenumbraService _penumbra;
    private readonly ResourceSourceAttributor _sourceAttributor;
    private readonly IPluginLog _log;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _reportedAmbiguities = new(StringComparer.Ordinal);

    private IReadOnlyList<OnScreenObject> _items = Array.Empty<OnScreenObject>();
    private bool _refreshing;

    public OnScreenService(
        IObjectTable objects,
        IClientState clientState,
        IFramework framework,
        PenumbraService penumbra,
        IPluginLog log)
    {
        _objects = objects;
        _clientState = clientState;
        _framework = framework;
        _penumbra = penumbra;
        _sourceAttributor = new ResourceSourceAttributor(penumbra, log);
        _log = log;
    }

    public bool IsRefreshing
    {
        get { lock (_lock) return _refreshing; }
    }

    public IReadOnlyList<OnScreenObject> Items
    {
        get { lock (_lock) return _items; }
    }

    public void RequestRefresh()
    {
        lock (_lock)
        {
            if (_refreshing)
                return;
            _refreshing = true;
        }

        try
        {
            _framework.RunOnFrameworkThread(CollectSnapshot)
                .ContinueWith(OnCollected, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            lock (_lock)
                _refreshing = false;
            _log.Debug($"Could not schedule on-screen refresh: {e.Message}");
        }
    }

    private void OnCollected(Task<List<OnScreenObject>> task)
    {
        if (task.IsFaulted)
            _log.Error(task.Exception?.GetBaseException(), "Failed to collect player resource trees.");

        lock (_lock)
        {
            _items = task.IsCompletedSuccessfully ? task.Result : Array.Empty<OnScreenObject>();
            _refreshing = false;
        }
    }

    private List<OnScreenObject> CollectSnapshot()
    {
        // IClientState has no LocalPlayer property in the installed Dalamud API. It is
        // injected to gate snapshots on a live client; IObjectTable.LocalPlayer is the
        // supported local-player identity source for this API version.
        if (!_clientState.IsLoggedIn || _objects.LocalPlayer is not { Address: not 0 } localPlayer)
            return [];

        var trees = _penumbra.GetPlayerResourceTrees();
        var treeEntries = trees.ToList();
        AddMissingOwnedTrees(treeEntries, localPlayer);
        var resolvedPaths = _penumbra.GetResourcePaths(treeEntries.Select(entry => entry.Key).ToArray());
        var result = new List<OnScreenObject>(trees.Count);
        for (var treeIndex = 0; treeIndex < treeEntries.Count; treeIndex++)
        {
            var (index, tree) = treeEntries[treeIndex];
            try
            {
                var candidate = _objects[index];
                if (candidate is null || candidate.Address == nint.Zero)
                {
                    ReportAmbiguityOnce("missing-candidate", "A Penumbra player-tree candidate was absent from the object table.");
                    continue;
                }

                if (!TryClassifyCandidate(candidate, localPlayer, out var category))
                    continue;

                IReadOnlyList<ResourceNode> roots = (tree.Nodes ?? [])
                    .Select(CopyPrunedNode)
                    .Where(node => node is not null)
                    .Cast<ResourceNode>()
                    .OrderBy(node => SectionOrder(node.ResourceSection))
                    .ThenBy(node => node.SortOrder)
                    .ToArray();
                var treeRootCount = roots.Count;
                roots = AddMissingResolvedModels(
                    roots,
                    treeIndex < resolvedPaths.Length ? resolvedPaths[treeIndex] : null,
                    _sourceAttributor.AttributionFor);
                if (roots.Count > treeRootCount)
                    _log.Debug($"Supplemented {roots.Count - treeRootCount} model resource(s) omitted from Penumbra's tree DTO for object {index}.");
                if (roots.Count == 0)
                    continue;

                result.Add(CreateSnapshot(index, candidate, category, roots));
            }
            catch (Exception e)
            {
                _log.Debug($"Could not copy player resource tree for object {index}: {e.Message}");
            }
        }

        return result;
    }

    private void AddMissingOwnedTrees(List<KeyValuePair<ushort, ResourceTreeDto>> treeEntries, IGameObject localPlayer)
    {
        var existing = treeEntries.Select(entry => entry.Key).ToHashSet();
        var missing = new List<ushort>();
        try
        {
            foreach (var candidate in _objects)
            {
                if (candidate is null || candidate.Address == nint.Zero ||
                    existing.Contains(candidate.ObjectIndex) ||
                    !IsSupportedOwnedObject(candidate) ||
                    !IsOwnedByLocalPlayer(candidate, localPlayer))
                    continue;

                missing.Add(candidate.ObjectIndex);
                existing.Add(candidate.ObjectIndex);
            }
        }
        catch (Exception e)
        {
            _log.Debug($"Could not enumerate owned objects for resource-tree fallback: {e.Message}");
            return;
        }

        if (missing.Count == 0)
            return;

        var recovered = _penumbra.GetResourceTrees(missing.ToArray());
        for (var i = 0; i < missing.Count && i < recovered.Length; i++)
        {
            var recoveredTree = recovered[i];
            if (recoveredTree is not null)
                treeEntries.Add(new KeyValuePair<ushort, ResourceTreeDto>(missing[i], recoveredTree));
        }
    }

    private static bool IsSupportedOwnedObject(IGameObject candidate)
        => candidate.ObjectKind is ObjectKind.Companion or ObjectKind.Mount or ObjectKind.FollowMount ||
           candidate is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Pet };

    private static bool IsOwnedByLocalPlayer(IGameObject candidate, IGameObject localPlayer)
        => candidate.OwnerId == localPlayer.EntityId ||
           (localPlayer.GameObjectId <= uint.MaxValue && candidate.OwnerId == (uint)localPlayer.GameObjectId);

    private bool TryClassifyCandidate(
        IGameObject candidate,
        IGameObject localPlayer,
        out ActorPresentationCategory category)
    {
        if (candidate.ObjectIndex == localPlayer.ObjectIndex && candidate.Address == localPlayer.Address)
        {
            category = ActorPresentationCategory.Player;
            return true;
        }

        // GetPlayerResourceTrees is already scoped by Penumbra to the player and their
        // owned objects. Do not apply a second OwnerId filter here: minion and mount
        // entries can expose an owner representation that differs from the local
        // player's object-table identity even though Penumbra returned their tree.
        switch (candidate.ObjectKind)
        {
            case ObjectKind.Companion:
                category = ActorPresentationCategory.Minion;
                return true;
            case ObjectKind.Mount:
            case ObjectKind.FollowMount:
                category = ActorPresentationCategory.Mount;
                return true;
            case ObjectKind.BattleNpc when candidate is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Pet }:
                category = ActorPresentationCategory.Summon;
                return true;
            default:
                ReportAmbiguityOnce($"unsupported:{candidate.ObjectKind}:{candidate.SubKind}", $"Omitting unsupported player-tree candidate {candidate.ObjectKind}/{candidate.SubKind}.");
                category = default;
                return false;
        }
    }

    private OnScreenObject CreateSnapshot(
        ushort objectIndex,
        IGameObject gameObject,
        ActorPresentationCategory category,
        IReadOnlyList<ResourceNode> roots)
        => new()
        {
            ObjectIndex = objectIndex,
            Address = gameObject.Address,
            Name = gameObject.Name.TextValue ?? "Unknown",
            PresentationCategory = category,
            ResourceRoots = roots,
        };

    /// <summary>
    /// Penumbra's tree DTO serializes its hierarchy and exposes a game path only when
    /// a node has exactly one possible path. The resource-path IPC walks the flat node
    /// set and retains every mapping, so use it to restore only missing, positively
    /// attributed model edit targets.
    /// </summary>
    internal static IReadOnlyList<ResourceNode> AddMissingResolvedModels(
        IReadOnlyList<ResourceNode> roots,
        Dictionary<string, HashSet<string>>? resolvedPaths,
        Func<string?, ResourceSource> attributeSource)
    {
        if (resolvedPaths is null || resolvedPaths.Count == 0)
            return roots;

        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Flatten(roots))
        {
            if (IsModelPath(node.GamePath) && IsModelPath(node.ActualPath))
                represented.Add(ModelKey(node.ActualPath, node.GamePath));
        }

        var supplemented = new List<ResourceNode>();
        foreach (var (actualPath, gamePaths) in resolvedPaths)
        {
            if (!IsModelPath(actualPath))
                continue;

            var source = attributeSource(actualPath);
            var normalizedActualPath = NormalizeActualPath(actualPath, source.State);
            var safeModModel = source.State == ResourceSourceState.LoadedMod && Path.IsPathRooted(actualPath);
            var safeGameModel = source.State == ResourceSourceState.GameData &&
                                !Path.IsPathRooted(normalizedActualPath) &&
                                PenumbraService.IsSafeGamePath(normalizedActualPath);
            if (!safeModModel && !safeGameModel)
                continue;

            foreach (var rawGamePath in gamePaths)
            {
                var gamePath = rawGamePath.Replace('\\', '/').TrimStart('/');
                if (!IsModelPath(gamePath) || !represented.Add(ModelKey(normalizedActualPath, gamePath)))
                    continue;

                var presentation = ResourcePresentation.For("Mdl", string.Empty, string.Empty, gamePath);
                supplemented.Add(new ResourceNode
                {
                    Type = "Mdl",
                    Icon = string.Empty,
                    Name = FileName(gamePath),
                    GamePath = gamePath,
                    ActualPath = normalizedActualPath,
                    Children = Array.Empty<ResourceNode>(),
                    SourceState = source.State,
                    SourceLabel = source.Label,
                    SourceModName = source.ModName,
                    SourceModDirectory = source.ModDirectory,
                    SourceModRootPath = source.ModRootPath,
                    SourceRelativePath = source.State == ResourceSourceState.GameData
                        ? normalizedActualPath
                        : source.RelativePath,
                    SlotLabel = presentation.SlotLabel,
                    ResourceSection = presentation.Section,
                    SortOrder = presentation.SortOrder,
                });
            }
        }

        if (supplemented.Count == 0)
            return roots;

        return roots.Concat(supplemented)
            .OrderBy(node => SectionOrder(node.ResourceSection))
            .ThenBy(node => node.SortOrder)
            .ThenBy(node => node.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsModelPath(string path)
        => path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    private static string ModelKey(string actualPath, string gamePath)
        => $"{actualPath.Replace('\\', '/')}\n{gamePath.Replace('\\', '/')}";

    private static string FileName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    internal static IReadOnlyList<ResourceNode> ProjectVisibleResourceNodes(
        IEnumerable<ResourceNode> roots,
        bool includeVanilla)
        => roots.SelectMany(node => ProjectVisibleResourceNode(node, includeVanilla)).ToArray();

    private static IEnumerable<ResourceNode> ProjectVisibleResourceNode(
        ResourceNode node,
        bool includeVanilla)
    {
        if (includeVanilla)
            return [node];

        var children = ProjectVisibleResourceNodes(node.Children, false);
        if (node.SourceState == ResourceSourceState.GameData)
            return children;
        if (node.Children.Count > 0 && children.Count == 0 &&
            node.SourceState != ResourceSourceState.LoadedMod)
            return [];
        return [node with { Children = children }];
    }

    internal static string NormalizeActualPath(string actualPath, ResourceSourceState sourceState)
        => sourceState == ResourceSourceState.GameData
            ? PathRules.NormalizeGamePath(actualPath)
            : actualPath;

    private ResourceNode? CopyPrunedNode(ResourceNodeDto node)
    {
        var children = (node.Children ?? [])
            .Select(CopyPrunedNode)
            .Where(child => child is not null)
            .Cast<ResourceNode>()
            .OrderBy(child => SectionOrder(child.ResourceSection))
            .ThenBy(child => child.SortOrder)
            .ToArray();
        var source = _sourceAttributor.AttributionFor(node.ActualPath);
        if (source.State is not (ResourceSourceState.LoadedMod or ResourceSourceState.GameData) && children.Length == 0)
            return null;

        var actualPath = NormalizeActualPath(node.ActualPath ?? string.Empty, source.State);
        var gamePath = PathRules.NormalizeGamePath(node.GamePath);
        var presentation = ResourcePresentation.For(node.Type.ToString(), node.Name ?? string.Empty, node.Icon.ToString(), gamePath);
        return new ResourceNode
        {
            Type = node.Type.ToString(),
            Icon = node.Icon.ToString(),
            Name = node.Name ?? string.Empty,
            GamePath = gamePath,
            ActualPath = actualPath,
            Children = children,
            SourceState = source.State,
            SourceLabel = source.Label,
            SourceModName = source.ModName,
            SourceModDirectory = source.ModDirectory,
            SourceModRootPath = source.ModRootPath,
            SourceRelativePath = source.State == ResourceSourceState.GameData
                ? actualPath
                : source.RelativePath,
            SlotLabel = presentation.SlotLabel,
            ResourceSection = presentation.Section,
            SortOrder = presentation.SortOrder,
        };
    }

    private void ReportAmbiguityOnce(string key, string message)
    {
        lock (_reportedAmbiguities)
        {
            if (!_reportedAmbiguities.Add(key))
                return;
        }

        _log.Debug(message);
    }

    private static int SectionOrder(ResourceSection section)
        => section switch
        {
            ResourceSection.CharacterFeatures => 0,
            ResourceSection.Gear => 1,
            _ => 2,
        };

    private static IEnumerable<ResourceNode> Flatten(IEnumerable<ResourceNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }
}

/// <summary>
/// Presentation adapter for Penumbra's resource tree.  A resource's game-path
/// namespace is the authoritative discriminator: a human-body model is a linked
/// body/skin resource, not an equipped body-slot item.  Labels remain a fallback
/// only for equipment IMC nodes, whose paths deliberately do not encode a slot.
/// </summary>
internal static class ResourcePresentation
{
    public static (string SlotLabel, ResourceSection Section, int SortOrder) For(string type, string name, string icon, string gamePath)
    {
        var path = gamePath.Replace('\\', '/').ToLowerInvariant();
        if (path.Contains("/obj/face/")) return ("Face", ResourceSection.CharacterFeatures, 0);
        if (path.Contains("/obj/hair/")) return ("Hair", ResourceSection.CharacterFeatures, 1);
        if (path.Contains("/obj/zear/")) return ("Ears", ResourceSection.CharacterFeatures, 2);
        if (path.Contains("/obj/tail/")) return ("Tail", ResourceSection.CharacterFeatures, 3);

        // Penumbra shows all top-level trees, including these connector resources.
        // XIV Instant Edit intentionally keeps them out of Gear: they are not the model
        // occupying an equipment slot and are therefore listed under Other instead.
        if (path.StartsWith("chara/equipment/", StringComparison.Ordinal) ||
            path.StartsWith("chara/accessory/", StringComparison.Ordinal) ||
            path.StartsWith("chara/weapon/", StringComparison.Ordinal))
            return GearPresentation(path, name, icon, type);

        return (string.IsNullOrWhiteSpace(name) ? "Other" : name, ResourceSection.Other, int.MaxValue);
    }

    private static (string SlotLabel, ResourceSection Section, int SortOrder) GearPresentation(string path, string name, string icon, string type)
    {
        if (path.EndsWith("_met.mdl", StringComparison.Ordinal)) return ("Head", ResourceSection.Gear, 0);
        if (path.EndsWith("_top.mdl", StringComparison.Ordinal)) return ("Body", ResourceSection.Gear, 1);
        if (path.EndsWith("_glv.mdl", StringComparison.Ordinal)) return ("Hands", ResourceSection.Gear, 2);
        if (path.EndsWith("_dwn.mdl", StringComparison.Ordinal)) return ("Legs", ResourceSection.Gear, 3);
        if (path.EndsWith("_sho.mdl", StringComparison.Ordinal)) return ("Feet", ResourceSection.Gear, 4);
        if (path.EndsWith("_ear.mdl", StringComparison.Ordinal)) return ("Earrings", ResourceSection.Gear, 5);
        if (path.EndsWith("_nek.mdl", StringComparison.Ordinal)) return ("Necklace", ResourceSection.Gear, 6);
        if (path.EndsWith("_wrs.mdl", StringComparison.Ordinal)) return ("Bracelet", ResourceSection.Gear, 7);
        if (path.EndsWith("_rir.mdl", StringComparison.Ordinal)) return ("Right Ring", ResourceSection.Gear, 8);
        if (path.EndsWith("_ril.mdl", StringComparison.Ordinal)) return ("Left Ring", ResourceSection.Gear, 9);

        // IMC nodes have no slot suffix. Their UI name is the same metadata
        // Penumbra displays, so use it only inside a canonical gear namespace.
        var semantic = $"{name} {icon} {type}".ToLowerInvariant();
        if (Contains(semantic, "head")) return ("Head", ResourceSection.Gear, 0);
        if (Contains(semantic, "body")) return ("Body", ResourceSection.Gear, 1);
        if (Contains(semantic, "hand")) return ("Hands", ResourceSection.Gear, 2);
        if (Contains(semantic, "leg")) return ("Legs", ResourceSection.Gear, 3);
        if (Contains(semantic, "feet") || Contains(semantic, "foot")) return ("Feet", ResourceSection.Gear, 4);
        if (Contains(semantic, "earring")) return ("Earrings", ResourceSection.Gear, 5);
        if (Contains(semantic, "necklace") || Contains(semantic, "neck")) return ("Necklace", ResourceSection.Gear, 6);
        if (Contains(semantic, "bracelet") || Contains(semantic, "wrist")) return ("Bracelet", ResourceSection.Gear, 7);
        if (Contains(semantic, "right ring")) return ("Right Ring", ResourceSection.Gear, 8);
        if (Contains(semantic, "left ring")) return ("Left Ring", ResourceSection.Gear, 9);
        return ("Weapon", ResourceSection.Gear, 10);
    }

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.Ordinal);
}
