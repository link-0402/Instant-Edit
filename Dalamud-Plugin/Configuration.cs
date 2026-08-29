using Dalamud.Configuration;
using InstantEdit.Models;

namespace InstantEdit;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 9;

    /// <summary> Port the Blender add-on listens on for import commands. </summary>
    public int BlenderPort { get; set; } = 42424;

    /// <summary> Port this plugin listens on for export results coming from Blender. </summary>
    public int ListenPort { get; set; } = 42428;

    /// <summary>Legacy managed-mod setting retained for configuration compatibility.</summary>

    /// <summary>Bind imported meshes to an existing Blender armature instead of creating one.</summary>
    public bool UseExistingSkeleton { get; set; }

    /// <summary>Name of the scene armature used when <see cref="UseExistingSkeleton"/> is enabled.</summary>
    public string SkeletonObjectName { get; set; } = "Skeleton";

    /// <summary>Create display-only Blender materials from the resolved FFXIV resources.</summary>
    public bool ApplyTexturesAndMaterials { get; set; }

    /// <summary>Skip body skin, body-piercing, and pube preview resources.</summary>
    public bool ExcludeBodyAndGeneralMaterials { get; set; }

    /// <summary>Show game-data resources alongside Penumbra-modified resources on screen.</summary>
    public bool IncludeVanillaResources { get; set; }

    /// <summary>Keep the main window visible when the user hides the game UI with Scroll Lock.</summary>
    public bool KeepVisibleWhenUiHidden { get; set; }

    /// <summary>Import contexts retained so saved Blender scenes can reconnect after a restart.</summary>
    public List<PersistedExportContext> ExportContexts { get; set; } = [];

}
