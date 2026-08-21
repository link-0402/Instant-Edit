using Dalamud.Configuration;

namespace InstantEdit;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    /// <summary> Port the Blender addon listens on for import commands. </summary>
    public int BlenderPort { get; set; } = 42424;

    /// <summary> Port this plugin listens on for export results coming from Blender. </summary>
    public int ListenPort { get; set; } = 42428;

    /// <summary> Directory name of the persistent mod used to apply exports. </summary>
    public string ModName { get; set; } = "InstantEdit";

}
