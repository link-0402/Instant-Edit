using System.Text.Json.Serialization;

namespace InstantEdit.Models;

/// <summary>Scene setup requested for a Blender model import.</summary>
public sealed record BlenderImportOptions
{
    public const string GeneratedMode = "generated";
    public const string ExistingMode = "existing";

    [JsonPropertyName("armatureMode")]
    public string ArmatureMode { get; init; } = GeneratedMode;

    [JsonPropertyName("targetObject")]
    public string TargetObject { get; init; } = "Skeleton";

    public static BlenderImportOptions Generated { get; } = new();

    public static BlenderImportOptions Existing(string targetObject)
        => new()
        {
            ArmatureMode = ExistingMode,
            TargetObject = string.IsNullOrWhiteSpace(targetObject) ? "Skeleton" : targetObject.Trim(),
        };
}
