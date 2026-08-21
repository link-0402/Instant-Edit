using System.Text.Json.Serialization;

namespace InstantEdit.Models;

/// <summary> The actor identity captured when an import is started. </summary>
public sealed record ActorIdentity
{
    [JsonPropertyName("objectIndex")]
    public required ushort ObjectIndex { get; init; }

    /// <summary> Native address at capture time; it is only an in-process identity token. </summary>
    [JsonPropertyName("address")]
    public required long Address { get; init; }
}

/// <summary> Versioned, plugin-owned context sent to the Blender addon. </summary>
public sealed record InstantEditImportContext
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "instant-edit.context";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("pluginInstanceId")]
    public required string PluginInstanceId { get; init; }

    [JsonPropertyName("contextId")]
    public required string ContextId { get; init; }

    [JsonPropertyName("importId")]
    public required string ImportId { get; init; }

    [JsonPropertyName("capability")]
    public required string Capability { get; init; }

    [JsonPropertyName("gamePath")]
    public required string GamePath { get; init; }

    [JsonPropertyName("objectIndex")]
    public required ushort ObjectIndex { get; init; }

    [JsonPropertyName("actorIdentity")]
    public ActorIdentity? ActorIdentity { get; init; }

    [JsonPropertyName("modName")]
    public required string ModName { get; init; }

    [JsonPropertyName("callbackPort")]
    public required int CallbackPort { get; init; }
}

/// <summary> A completed export operation retained for idempotent retries. </summary>
public sealed record ExportReceipt(bool Success, string Code, string Message);
