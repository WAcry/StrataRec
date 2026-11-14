using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Strict (de)serialization of artifact models: snake_case names, comments and trailing commas allowed (JSONC),
/// unknown properties rejected so that typos surface as errors instead of being ignored. A member an earlier format
/// had is rejected with what replaced it (<see cref="RetiredMembers"/>). Every other mismatch is described in JSON words
/// by <see cref="JsonBindingErrors"/>, the same way for artifacts and for the parts of them bound later (operator
/// configs, nodes, patch sections).
/// </summary>
public static class ArtifactSerializer
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Every artifact type by its name, as <c>stratarec schema &lt;name&gt;</c> and messages name it. One table, so the
    /// schema command, the workspace's hints and binding errors agree.
    /// </summary>
    public static IReadOnlyList<(string Name, Type Type)> ArtifactTypes { get; } = new (string, Type)[]
    {
        ("pipeline", typeof(PipelineArtifact)),
        ("catalog", typeof(FeatureCatalogArtifact)),
        ("bindings", typeof(SourceBindingArtifact)),
        ("experiment", typeof(ExperimentArtifact)),
        ("release", typeof(ReleaseArtifact)),
        ("policy", typeof(PolicyArtifact)),
        ("table", typeof(DataTableArtifact)),
        ("manifest", typeof(ReleaseManifest)),
        ("replay_bundle", typeof(ReplayBundle)),
    };

    /// <summary>The name of an artifact type in <see cref="ArtifactTypes"/>, or null for another type.</summary>
    public static string? ArtifactName(Type type)
    {
        foreach (var (name, artifactType) in ArtifactTypes)
        {
            if (artifactType == type)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Deserializes an artifact document.</summary>
    /// <exception cref="ArtifactFormatException">The document does not match <typeparamref name="T"/>; the exception says where and why, in JSON words.</exception>
    public static T Deserialize<T>(JsonNode node, string? sourceName = null)
    {
        try
        {
            if (node.Deserialize<T>(Options) is { } value)
            {
                return value;
            }
        }
        catch (JsonException ex)
        {
            throw RetiredMembers.Find(typeof(T), node, sourceName)
                  ?? Failed(JsonBindingErrors.Describe(ex, node, typeof(T), Options, Subject(typeof(T))), sourceName, ex);
        }
        catch (Exception ex) when (NotBindable(ex, node, typeof(T), Subject(typeof(T))) is { } error)
        {
            throw Failed(error, sourceName, ex);
        }

        throw Failed(JsonBindingErrors.Null(typeof(T), Options, Subject(typeof(T))), sourceName, null);
    }

    /// <inheritdoc cref="Deserialize{T}(JsonNode, string?)"/>
    public static T Deserialize<T>(string json, string? sourceName = null)
    {
        try
        {
            if (JsonSerializer.Deserialize<T>(json, Options) is { } value)
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // The document is read again strictly, as a workspace file is: a string or key that is not valid Unicode,
            // a key written twice or a syntax error is told at its line and column, and the translator only ever walks
            // a document every part of which can be read.
            JsonNode? document;
            try
            {
                document = Jsonc.Parse(json, sourceName);
            }
            catch (JsoncException jsonc)
            {
                throw Unreadable(jsonc, sourceName);
            }

            if (RetiredMembers.Find(typeof(T), document, sourceName) is { } retired)
            {
                throw retired;
            }

            var error = ex is JsonException binding
                ? JsonBindingErrors.Describe(binding, document, typeof(T), Options, Subject(typeof(T)))
                : JsonBindingErrors.MissingDiscriminator(document, typeof(T), Options, Subject(typeof(T)));
            if (error is null)
            {
                throw; // not about the document
            }

            throw Failed(error, sourceName, ex);
        }

        throw Failed(JsonBindingErrors.Null(typeof(T), Options, Subject(typeof(T))), sourceName, null);
    }

    /// <summary>
    /// Binds part of a document strictly, e.g. an operator's config, a node or a section of a patch: unknown keys are
    /// errors, as everywhere else. Returns false with the error located relative to <paramref name="node"/> and worded
    /// for the person who wrote it (see <see cref="JsonBindingErrors"/>).
    /// </summary>
    /// <param name="subject">How a sentence names <paramref name="node"/>, e.g. <c>the config of TopK@1</c>.</param>
    public static bool TryBind<T>(JsonNode? node, string subject, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out JsonBindingError? error)
        where T : class
    {
        try
        {
            value = node.Deserialize<T>(Options);
        }
        catch (JsonException ex)
        {
            value = null;
            error = JsonBindingErrors.Describe(ex, node, typeof(T), Options, subject);
            return false;
        }
        catch (Exception ex) when (NotBindable(ex, node, typeof(T), subject) is { } found)
        {
            value = null;
            error = found;
            return false;
        }

        error = value is null ? JsonBindingErrors.Null(typeof(T), Options, subject) : null;
        return value is not null;
    }

    /// <summary>
    /// The error for a document the serializer rejects with an exception other than a <see cref="JsonException"/>: one
    /// with a part that cannot be read (writing a leniently parsed node reads it), or an object of an abstract
    /// polymorphic type without its discriminator. Null when the exception is not about the document, so it propagates.
    /// </summary>
    private static JsonBindingError? NotBindable(Exception exception, JsonNode? node, Type type, string subject) => exception switch
    {
        InvalidOperationException or ArgumentException => JsonBindingErrors.Unreadable(node, subject),
        NotSupportedException => JsonBindingErrors.MissingDiscriminator(node, type, Options, subject),
        _ => null,
    };

    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;

    /// <summary>How a sentence names a document of <paramref name="type"/>: <c>the pipeline</c>, <c>the replay bundle</c>.</summary>
    private static string Subject(Type type) => ArtifactName(type) is { } name ? "the " + name.Replace('_', ' ') : "the document";

    private static ArtifactFormatException Failed(JsonBindingError error, string? sourceName, Exception? inner) =>
        new(sourceName, error.Pointer, error.Message, inner, error.Hint);

    /// <summary>A document the JSONC reader rejects (syntax, invalid Unicode, a duplicate key): it says where, with a line and column.</summary>
    private static ArtifactFormatException Unreadable(JsoncException ex, string? sourceName)
    {
        var where = ex.Line is { } line ? $" (line {line}, column {ex.Column})" : string.Empty;
        return new ArtifactFormatException(sourceName, string.Empty, ex.Reason + where, ex, ex.Hint);
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
    public ArtifactFormatException(string? source, string pointer, string message, Exception? inner = null, string? hint = null)
        : base($"{source ?? "<artifact>"}: {(pointer.Length > 0 ? pointer + ": " : string.Empty)}{message}", inner)
    {
        ArtifactSource = source;
        Pointer = pointer;
        Reason = message;
        Hint = hint;
    }

    public string? ArtifactSource { get; }

    /// <summary>What is wrong, without the source and pointer that <see cref="Exception.Message"/> starts with.</summary>
    public string Reason { get; }

    /// <summary>How to fix it, when the reader knows better than "see the schema".</summary>
    public string? Hint { get; }

    /// <summary>RFC 6901 pointer of the value at fault; empty for the document as a whole.</summary>
    public string Pointer { get; }
}
