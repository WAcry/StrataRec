using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Strict (de)serialization of artifact models: snake_case names, comments and trailing commas allowed (JSONC),
/// unknown properties rejected so that typos surface as errors instead of being ignored. Every mismatch is described in JSON words
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
                return NullElementOf<T>(node) is { } nullElement ? throw Failed(nullElement, sourceName, null) : value;
            }
        }
        catch (JsonException ex)
        {
            throw Failed(JsonBindingErrors.Describe(ex, node, typeof(T), Options, Subject(typeof(T))), sourceName, ex);
        }
        catch (Exception ex) when (NotBindable(ex, node, typeof(T), Options, Subject(typeof(T))) is { } error)
        {
            throw Failed(error, sourceName, ex);
        }

        throw Failed(JsonBindingErrors.Null(typeof(T), Options, Subject(typeof(T))), sourceName, null);
    }

    /// <inheritdoc cref="Deserialize{T}(JsonNode, string?)"/>
    /// <remarks>
    /// The text is checked as a workspace file is before it is bound (<see cref="Jsonc"/>: a key written twice, text that
    /// is not valid Unicode, a syntax error, told at its line and column), so a document read from a string (a manifest,
    /// a replay bundle, an artifact of the store) is accepted or rejected as the same document read as a file. The
    /// serializer alone keeps the last of two equal keys.
    /// </remarks>
    public static T Deserialize<T>(string json, string? sourceName = null)
    {
        ReadOnlyMemory<byte> utf8;
        bool hasNull;
        try
        {
            utf8 = Jsonc.Checked(json, sourceName, out hasNull);
        }
        catch (JsoncException ex)
        {
            throw Unreadable(ex, sourceName);
        }

        try
        {
            if (JsonSerializer.Deserialize<T>(utf8.Span, Options) is { } value)
            {
                return hasNull && NullElements.Find(Jsonc.ParseElement(utf8.Span, sourceName), typeof(T), Options) is { } nullElement
                    ? throw Failed(nullElement, sourceName, null)
                    : value;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Described on the document, which the check above has read already: the translator walks a tree.
            var document = Jsonc.Parse(json, sourceName);

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
        catch (Exception ex) when (NotBindable(ex, node, typeof(T), Options, subject) is { } found)
        {
            value = null;
            error = found;
            return false;
        }

        if (value is null)
        {
            error = JsonBindingErrors.Null(typeof(T), Options, subject);
            return false;
        }

        error = NullElementOf<T>(node);
        if (error is not null)
        {
            value = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="TryBind{T}(JsonNode, string, out T, out JsonBindingError)"/> for a document given as compact UTF-8 JSON,
    /// with options that derive from <see cref="Options"/> (a binding's settings add converters of their own,
    /// <see cref="BindingSettings"/>). Deserialized from the bytes themselves, so a converter's
    /// <see cref="Utf8JsonReader.TokenStartIndex"/> is an offset into them. An exception that is not about the document,
    /// such as a <see cref="Core.Bindings.BindingException"/> a converter throws, propagates.
    /// </summary>
    internal static bool TryBind<T>(
        ReadOnlySpan<byte> utf8, JsonSerializerOptions options, string subject, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out JsonBindingError? error)
        where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(utf8, options);
        }
        catch (JsonException ex)
        {
            value = null;
            error = JsonBindingErrors.Describe(ex, JsonNode.Parse(utf8), typeof(T), options, subject);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException
                                   && NotBindable(ex, JsonNode.Parse(utf8), typeof(T), options, subject) is { } found)
        {
            value = null;
            error = found;
            return false;
        }

        if (value is null)
        {
            error = JsonBindingErrors.Null(typeof(T), options, subject);
            return false;
        }

        error = HasNull(utf8) ? NullElements.Find(JsonSerializer.Deserialize<JsonElement>(utf8), typeof(T), options) : null;
        if (error is not null)
        {
            value = null;
            return false;
        }

        return true;
    }

    /// <summary>A null element of <paramref name="node"/> where <typeparamref name="T"/> requires a value; walked only when it has a null.</summary>
    private static JsonBindingError? NullElementOf<T>(JsonNode? node) =>
        NullElements.AnyNull(node) ? NullElements.Find(JsonSerializer.SerializeToElement(node, Options), typeof(T), Options) : null;

    /// <summary>Whether the JSON has a <c>null</c> anywhere: without one, no null element needs looking for.</summary>
    private static bool HasNull(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The error for a document the serializer rejects with an exception other than a <see cref="JsonException"/>: one
    /// with a part that cannot be read (writing a leniently parsed node reads it), or an object of an abstract
    /// polymorphic type without its discriminator. Null when the exception is not about the document, so it propagates.
    /// </summary>
    private static JsonBindingError? NotBindable(Exception exception, JsonNode? node, Type type, JsonSerializerOptions options, string subject) => exception switch
    {
        InvalidOperationException or ArgumentException => JsonBindingErrors.Unreadable(node, subject),
        NotSupportedException => JsonBindingErrors.MissingDiscriminator(node, type, options, subject),
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

            // null where a member is not nullable is an error at its pointer, as the generated JSON Schema says: bound,
            // it was a null an operator or loader dereferenced (review 44). Elements of collections are not covered.
            RespectNullableAnnotations = true,

            // A key written twice is an error, as the JSONC reader says (Deserialize checks the text first, with a line and
            // column); set here too for a host that binds with these options itself.
            AllowDuplicateProperties = false,
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
