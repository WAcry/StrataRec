using System.Text.Json;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// Serializes <see cref="JsonPointer"/> as its RFC 6901 string, both as a value and as a dictionary key, so model
/// types can declare pointer-typed members directly (for example the policy's <c>experiment_patchable</c> list and
/// the <c>owners</c> map keyed by pointer, design 11.4).
/// </summary>
public sealed class JsonPointerJsonConverter : JsonConverter<JsonPointer>
{
    public override JsonPointer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected an RFC 6901 JSON pointer string such as \"/nodes/fusion\" but found {reader.TokenType}.");
        }

        return ParseOrThrow(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, JsonPointer value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.ToString());
    }

    public override JsonPointer ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ParseOrThrow(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, JsonPointer value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WritePropertyName(value.ToString());
    }

    private static JsonPointer ParseOrThrow(string text) =>
        JsonPointer.TryParseCore(text, out var pointer, out var error) ? pointer : throw new JsonException(error);
}
