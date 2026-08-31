using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Models;

/// <summary>
/// Persists a System.Text.Json array through Dalamud's Newtonsoft.Json-based
/// configuration serializer without exposing JsonNode.Parent reference cycles.
/// </summary>
public sealed class JsonArrayNewtonsoftConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
        => typeof(JsonArray).IsAssignableFrom(objectType);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not JsonArray array)
        {
            writer.WriteNull();
            return;
        }

        JToken.Parse(array.ToJsonString()).WriteTo(writer);
    }

    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        JsonSerializer serializer)
    {
        var token = JToken.Load(reader);
        if (token.Type == JTokenType.Null)
            return new JsonArray();

        try
        {
            return JsonNode.Parse(token.ToString(Formatting.None))?.AsArray() ?? new JsonArray();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new JsonSerializationException("The persisted manipulation snapshot is not a JSON array.", exception);
        }
    }
}
