using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Helpers;

namespace InstantEdit.Services;

/// <summary>
/// Captures only the local player's Penumbra resource trees. Penumbra selects the
/// candidate indices; each is then joined to the object table and fail-closed ownership
/// rules before its immutable, pruned snapshot is published.
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

    /// <summary> Returns the captured native identity for an object in the current snapshot. </summary>
    public ActorIdentity? GetActorIdentity(int objectIndex)
    {
        if (objectIndex is < 0 or > ushort.MaxValue)
            return null;

        lock (_lock)
            return _items.FirstOrDefault(item => item.ObjectIndex == objectIndex)?.ActorIdentity;
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

        if (localPlayer.GameObjectId > uint.MaxValue)
        {
            ReportAmbiguityOnce("local-owner-id", "Local player owner ID is not representable; omitting owned resource trees.");
            return [];
        }

        var localOwnerId = (uint)localPlayer.GameObjectId;
        var trees = _penumbra.GetPlayerResourceTrees();
        var result = new List<OnScreenObject>(trees.Count);
        foreach (var (index, tree) in trees)
        {
            try
            {
                var candidate = _objects[index];
                if (candidate is null || candidate.Address == nint.Zero)
                {
                    ReportAmbiguityOnce("missing-candidate", "A Penumbra player-tree candidate was absent from the object table.");
                    continue;
                }

                if (!TryClassifyCandidate(candidate, localPlayer, localOwnerId, out var category))
                    continue;

                var roots = (tree.Nodes ?? [])
                    .Select(CopyPrunedNode)
                    .Where(node => node is not null)
                    .Cast<ResourceNode>()
                    .OrderBy(node => SectionOrder(node.ResourceSection))
                    .ThenBy(node => node.SortOrder)
                    .ToArray();
                if (roots.Length == 0)
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

    private bool TryClassifyCandidate(
        IGameObject candidate,
        IGameObject localPlayer,
        uint localOwnerId,
        out ActorPresentationCategory category)
    {
        if (candidate.ObjectIndex == localPlayer.ObjectIndex && candidate.Address == localPlayer.Address)
        {
            category = ActorPresentationCategory.Player;
            return true;
        }

        if (candidate.OwnerId != localOwnerId)
        {
            ReportAmbiguityOnce($"owner-mismatch:{candidate.ObjectKind}", $"Omitting {candidate.ObjectKind} player-tree candidate with a non-local owner ID.");
            category = default;
            return false;
        }

        switch (candidate.ObjectKind)
        {
            case ObjectKind.Companion:
                category = ActorPresentationCategory.Minion;
                return true;
            case ObjectKind.Mount:
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
            ActorIdentity = new ActorIdentity { ObjectIndex = objectIndex, Address = gameObject.Address.ToInt64() },
            Name = gameObject.Name.TextValue ?? "Unknown",
            Kind = gameObject.ObjectKind.ToString(),
            PresentationCategory = category,
            ResourceRoots = roots,
            // Compatibility edit targets are sourced from the same retained Penumbra
            // nodes; no independent model scan, inference, or de-duplication occurs.
            Models = Flatten(roots)
                .Where(node => node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                .Select(node => new MdlFile { GamePath = node.GamePath, LocalPath = node.ActualPath })
                .ToArray(),
        };

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
        var isModdedSubtree = source.State == ResourceSourceState.LoadedMod || children.Any(child => child.IsModdedSubtree);
        if (!isModdedSubtree)
            return null;

        var presentation = ResourcePresentation.For(node.Type.ToString(), node.Name ?? string.Empty, node.Icon.ToString(), node.GamePath ?? string.Empty);
        return new ResourceNode
        {
            Type = node.Type.ToString(),
            Icon = node.Icon.ToString(),
            Name = node.Name ?? string.Empty,
            GamePath = node.GamePath ?? string.Empty,
            ActualPath = node.ActualPath ?? string.Empty,
            Children = children,
            SourceState = source.State,
            SourceLabel = source.Label,
            SourceModName = source.ModName,
            SourceRelativePath = source.RelativePath,
            SlotLabel = presentation.SlotLabel,
            ResourceSection = presentation.Section,
            SortOrder = presentation.SortOrder,
            IsModdedSubtree = true,
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
        // Instant Edit intentionally keeps them out of Gear: they are not the model
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
