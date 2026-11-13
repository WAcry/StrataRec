using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Strict (de)serialization of artifact models: snake_case names, comments and trailing commas allowed (JSONC),
/// unknown properties rejected so that typos surface as errors instead of being ignored. A member an earlier format
/// had is rejected with what replaced it (<see cref="RetiredMembers"/>).
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
            throw RetiredMembers.Find(typeof(T), node, sourceName) ?? new ArtifactFormatException(sourceName, ex.Path ?? "$", ex.Message, ex);
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
            throw RetiredMembers.Find(typeof(T), json, sourceName) ?? new ArtifactFormatException(sourceName, ex.Path ?? "$", ex.Message, ex);
        }
    }

    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;

    /// <summary>Converts System.Text.Json's <c>$.a.b</c> / <c>$['a.b']</c> error paths to an RFC 6901 pointer, so deserialization errors say where in the document they are.</summary>
    public static string JsonPathToPointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        var i = path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            string token;
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                {
                    end++;
                }

                token = path[(i + 1)..end];
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                var end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }

                token = path[(i + 2)..end];
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                if (end < 0)
                {
                    break;
                }

                token = path[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                break;
            }

            builder.Append('/').Append(Json.JsonPointer.Escape(token));
        }

        return builder.ToString();
    }

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
    public ArtifactFormatException(string? source, string path, string message, Exception? inner = null, string? hint = null)
        : base($"{source ?? "<artifact>"}: {path}: {message}", inner)
    {
        ArtifactSource = source;
        JsonPath = path;
        Reason = message;
        Hint = hint;
    }

    public string? ArtifactSource { get; }

    /// <summary>What is wrong, without the source and path that <see cref="Exception.Message"/> starts with.</summary>
    public string Reason { get; }

    /// <summary>How to fix it, when the reader knows better than "see the schema".</summary>
    public string? Hint { get; }

    /// <summary>Location of the problem as a JSONPath-like string from System.Text.Json.</summary>
    public string JsonPath { get; }
}
