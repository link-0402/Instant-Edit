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

    [JsonPropertyName("applyTexturesAndMaterials")]
    public bool ApplyTexturesAndMaterials { get; init; }

    [JsonPropertyName("excludeBodyAndGeneralMaterials")]
    public bool ExcludeBodyAndGeneralMaterials { get; init; }

    public static BlenderImportOptions Generated { get; } = new();

    public static BlenderImportOptions GeneratedWithPreview(
        bool applyTexturesAndMaterials,
        bool excludeBodyAndGeneralMaterials = false)
        => new()
        {
            ApplyTexturesAndMaterials = applyTexturesAndMaterials,
            ExcludeBodyAndGeneralMaterials = applyTexturesAndMaterials && excludeBodyAndGeneralMaterials,
        };

    public static BlenderImportOptions Existing(
        string targetObject,
        bool applyTexturesAndMaterials = false,
        bool excludeBodyAndGeneralMaterials = false)
        => new()
        {
            ArmatureMode = ExistingMode,
            TargetObject = string.IsNullOrWhiteSpace(targetObject) ? "Skeleton" : targetObject.Trim(),
            ApplyTexturesAndMaterials = applyTexturesAndMaterials,
            ExcludeBodyAndGeneralMaterials = applyTexturesAndMaterials && excludeBodyAndGeneralMaterials,
        };
}
