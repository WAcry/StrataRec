using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Strict (de)serialization of artifact models: snake_case names, comments and trailing commas allowed (JSONC),
/// unknown properties rejected so that typos surface as errors instead of being ignored.
/// </summary>
public static class ArtifactSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static T Deserialize<T>(JsonNode node, string? sourceName = null)
    {
        try
        {
            return node.Deserialize<T>(Options)
                   ?? throw new ArtifactFormatException(sourceName, "$", $"Expected a {typeof(T).Name} object, got null.");
        }
        catch (JsonException ex)
        {
            throw new ArtifactFormatException(sourceName, ex.Path ?? "$", ex.Message, ex);
        }
    }

    public static T Deserialize<T>(string json, string? sourceName = null)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                   ?? throw new ArtifactFormatException(sourceName, "$", $"Expected a {typeof(T).Name} object, got null.");
        }
        catch (JsonException ex)
        {
            throw new ArtifactFormatException(sourceName, ex.Path ?? "$", ex.Message, ex);
        }
    }

    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>An artifact that does not match its schema.</summary>
public sealed class ArtifactFormatException : Exception
{
    public ArtifactFormatException(string? source, string path, string message, Exception? inner = null)
        : base($"{source ?? "<artifact>"}: {path}: {message}", inner)
    {
        ArtifactSource = source;
        JsonPath = path;
    }

    public string? ArtifactSource { get; }

    /// <summary>Location of the problem as a JSONPath-like string from System.Text.Json.</summary>
    public string JsonPath { get; }
}
