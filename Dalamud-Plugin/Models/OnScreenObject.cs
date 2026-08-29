namespace InstantEdit.Models;

/// <summary> How confidently a resolved resource path can be attributed. </summary>
public enum ResourceSourceState
{
    GameData,
    LoadedMod,
    ExternalResolvedFile,
    SourceUnavailable,
}

/// <summary> Actor categories admitted to the on-screen resource snapshot. </summary>
public enum ActorPresentationCategory
{
    Player,
    Minion,
    Mount,
    Summon,
}

/// <summary> Display grouping for modded Penumbra resource nodes. </summary>
public enum ResourceSection
{
    CharacterFeatures,
    Gear,
    Other,
}

/// <summary>
/// Immutable, plugin-owned copy of a node returned by Penumbra's resource-tree IPC.
/// It contains no live resource handles and is therefore safe to retain after a refresh.
/// </summary>
public sealed record ResourceNode
{
    public required string Type { get; init; }
    public required string Icon { get; init; }
    public required string Name { get; init; }
    public required string GamePath { get; init; }
    public required string ActualPath { get; init; }
    public required IReadOnlyList<ResourceNode> Children { get; init; }
    public required string SourceLabel { get; init; }
    public string? SourceModName { get; init; }
    public string? SourceModDirectory { get; init; }
    public string? SourceModRootPath { get; init; }
    public string? SourceRelativePath { get; init; }
    public required string SlotLabel { get; init; }
    public required ResourceSection ResourceSection { get; init; }
    public required int SortOrder { get; init; }
}

/// <summary> A model selected for import into Blender. </summary>
public sealed class MdlFile
{
    public required string GamePath { get; init; }
    public required string LocalPath { get; init; }
    public bool IsFilePath => Path.IsPathRooted(LocalPath);
    public bool IsModded => IsFilePath && !string.Equals(GamePath, LocalPath, StringComparison.OrdinalIgnoreCase);
    public string FileName => GamePath[(GamePath.LastIndexOf('/') + 1)..];
}

/// <summary> A game object currently drawn on screen and its Penumbra resource hierarchy. </summary>
public sealed record OnScreenObject
{
    public required ushort ObjectIndex { get; init; }
    public required nint Address { get; init; }
    public required string Name { get; init; }
    public required ActorPresentationCategory PresentationCategory { get; init; }
    public required IReadOnlyList<ResourceNode> ResourceRoots { get; init; }
}
