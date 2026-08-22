using Dalamud.Configuration;

namespace InstantEdit;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary> Port the Blender addon listens on for import commands. </summary>
    public int BlenderPort { get; set; } = 42424;

    /// <summary> Port this plugin listens on for export results coming from Blender. </summary>
    public int ListenPort { get; set; } = 42428;

    /// <summary>Legacy managed-mod setting retained for configuration compatibility.</summary>
    public string ModName { get; set; } = "InstantEdit";

    /// <summary>Bind imported meshes to an existing Blender armature instead of creating one.</summary>
    public bool UseExistingSkeleton { get; set; }

    /// <summary>Name of the scene armature used when <see cref="UseExistingSkeleton"/> is enabled.</summary>
    public string SkeletonObjectName { get; set; } = "Skeleton";

}
