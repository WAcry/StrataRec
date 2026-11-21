using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StrataRec.Artifacts.Json;
using StrataRec.Core.Bindings;

namespace StrataRec.Artifacts.Model;

/// <summary>
/// Binds a binding's own settings to the C# type its factory declares (design 6.4), as an operator's config is bound
/// (design 9.1): snake_case, strict, and every mistake worded by <see cref="JsonBindingErrors"/> (an unknown member with
/// the one it resembles, a value of the wrong kind, a missing required member with what it means), here as a
/// <see cref="BindingException"/> at its pointer within the binding, with a hint. The settings type is also the single
/// source of the settings' JSON Schema (<see cref="ArtifactSchemas"/>): what an editor checks is what binds, and no
/// factory parses JSON by hand.
/// </summary>
/// <remarks>
/// <para>A member of type <see cref="LocalFile"/> or <see cref="SecretReference"/> is bound by the binding's context
/// (<see cref="BindingContext.LocalFile"/>, <see cref="BindingContext.SecretReference"/>): a path resolved against the
/// pinned base directory, a reference resolved through the host's store at load, each knowing its setting's pointer for
/// the errors of the load. The serializer gives a converter no state of the call, so the binding in progress is the
/// thread's (deserialization is synchronous), and a converter finds its value's pointer from the value's offset in the
/// document being bound, which is why the settings are bound from bytes written here.</para>
/// <para>Compile time only: factories bind when they create an instance, never on a request.</para>
/// </remarks>
public static class BindingSettings
{
    /// <summary>The hint of an error <see cref="JsonBindingErrors"/> has none for.</summary>
    private const string SchemaHint = "'stratarec schema bindings' prints the JSON Schema of bindings, which describes the settings of each type this binary has";

    /// <summary>The binding being bound on this thread, for the converters; null outside <see cref="Bind{T}(BindingContext, string)"/>.</summary>
    [ThreadStatic]
    private static Scope? t_scope;

    /// <summary>
    /// <see cref="ArtifactSerializer.Options"/> with the converters of <see cref="LocalFile"/> and
    /// <see cref="SecretReference"/>: what settings bind with, and what their schema is exported with.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Binds the binding's own settings (<see cref="BindingContext.Settings"/>) to <typeparamref name="T"/>.</summary>
    /// <param name="type">The binding's type, e.g. <c>linear@1</c>, as messages name the settings.</param>
    /// <exception cref="BindingException">The settings do not bind: at the setting's pointer, with a hint.</exception>
    public static T Bind<T>(BindingContext context, string type)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(type);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in context.Settings)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Bind<T>(context, buffer.WrittenMemory, string.Empty, "the settings of " + type);
    }

    /// <summary>
    /// Binds one value within the settings to <typeparamref name="T"/>, as <see cref="Bind{T}(BindingContext, string)"/>
    /// binds them all: for a member a factory takes in more than one form (a list or a map), declared as a
    /// <see cref="JsonElement"/> and bound once its form is known, or for a file of settings.
    /// </summary>
    /// <param name="pointer">The value's pointer within the binding, e.g. <c>/inputs</c>; errors are reported below it.</param>
    /// <param name="subject">How a sentence names the value, e.g. <c>'inputs' of random@1</c>.</param>
    /// <exception cref="BindingException">The value does not bind: at its pointer within the binding, with a hint.</exception>
    public static T Bind<T>(BindingContext context, JsonElement value, string pointer, string subject)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(subject);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            value.WriteTo(writer);
        }

        return Bind<T>(context, buffer.WrittenMemory, pointer, subject);
    }

    private static T Bind<T>(BindingContext context, ReadOnlyMemory<byte> utf8, string prefix, string subject)
        where T : class
    {
        if (NonFiniteNumber(utf8.Span, out var literal) is var at and >= 0)
        {
            // The serializer binds it to infinity (a file's reader rejects it, but settings built in code can hold it).
            throw new BindingException(prefix + PointerAt(utf8.Span, at), $"The number {literal} is outside the double-precision range.",
                "use a smaller magnitude");
        }

        var outer = t_scope;
        t_scope = new Scope(context, utf8, prefix);
        try
        {
            if (ArtifactSerializer.TryBind<T>(utf8.Span, Options, subject, out var settings, out var error))
            {
                return settings;
            }

            // A file's reader rejects a key written twice, but settings built in code can hold one, which the serializer
            // reports at the object it is in.
            throw TryFindDuplicate(utf8.Span, out var pointer, out var key)
                ? new BindingException(prefix + pointer, $"Duplicate key '{key}'; a key appears once in an object.", "remove or merge one of the definitions")
                : new BindingException(prefix + error.Pointer, error.Message, error.Hint ?? SchemaHint);
        }
        finally
        {
            t_scope = outer;
        }
    }

    /// <summary>The offset of the first number of <paramref name="utf8"/> that is not finite as a double (<c>1e400</c>); -1 if none.</summary>
    internal static long NonFiniteNumber(ReadOnlySpan<byte> utf8, out string? literal)
    {
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Number && (!reader.TryGetDouble(out var number) || !double.IsFinite(number)))
            {
                literal = Encoding.UTF8.GetString(reader.ValueSpan);
                return reader.TokenStartIndex;
            }
        }

        literal = null;
        return -1;
    }

    /// <summary>The first key written twice in one object of <paramref name="utf8"/>, and the pointer of its second occurrence.</summary>
    internal static bool TryFindDuplicate(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out string? pointer, [NotNullWhen(true)] out string? key)
    {
        var reader = new Utf8JsonReader(utf8);
        var frames = new List<Frame>();
        var keys = new List<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    key = reader.GetString()!;
                    frames[^1] = new Frame(false, 0, key);
                    if (!(keys[^1] ??= new HashSet<string>(StringComparer.Ordinal)).Add(key))
                    {
                        pointer = Pointer(frames);
                        return true;
                    }

                    continue;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    frames.RemoveAt(frames.Count - 1);
                    keys.RemoveAt(keys.Count - 1);
                    continue;
            }

            if (frames.Count > 0 && frames[^1].IsArray)
            {
                frames[^1] = frames[^1] with { Index = frames[^1].Index + 1 };
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                frames.Add(new Frame(reader.TokenType == JsonTokenType.StartArray, -1, null));
                keys.Add(null);
            }
        }

        pointer = key = null;
        return false;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ArtifactSerializer.Options);
        options.Converters.Add(new LocalFileConverter());
        options.Converters.Add(new SecretReferenceConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// The RFC 6901 pointer of the value that starts at byte <paramref name="offset"/> of <paramref name="utf8"/>, by
    /// reading the document to it: the keys of the objects and the indexes of the arrays the value is in.
    /// </summary>
    internal static string PointerAt(ReadOnlySpan<byte> utf8, long offset)
    {
        var reader = new Utf8JsonReader(utf8);
        var frames = new List<Frame>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    frames[^1] = new Frame(false, 0, reader.GetString());
                    continue;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    frames.RemoveAt(frames.Count - 1);
                    continue;
            }

            if (frames.Count > 0 && frames[^1].IsArray)
            {
                frames[^1] = frames[^1] with { Index = frames[^1].Index + 1 };
            }

            if (reader.TokenStartIndex == offset)
            {
                return Pointer(frames);
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                frames.Add(new Frame(reader.TokenType == JsonTokenType.StartArray, -1, null));
            }
        }

        throw new ArgumentOutOfRangeException(nameof(offset), offset, "No value starts at this offset.");
    }

    private static string Pointer(List<Frame> frames)
    {
        var pointer = new StringBuilder();
        foreach (var frame in frames)
        {
            pointer.Append('/').Append(frame.IsArray ? frame.Index.ToString(CultureInfo.InvariantCulture) : JsonPointer.Escape(frame.Name!));
        }

        return pointer.ToString();
    }

    /// <summary>An object or array the reader is in: the key of its current member, or the index of its current element.</summary>
    private readonly record struct Frame(bool IsArray, int Index, string? Name);

    /// <summary>The binding being bound on a thread: its context, the document's bytes and where the document is within the binding.</summary>
    private sealed class Scope(BindingContext context, ReadOnlyMemory<byte> utf8, string prefix)
    {
        public BindingContext Context { get; } = context;

        /// <summary>The binding being bound on this thread.</summary>
        /// <exception cref="InvalidOperationException">None is: <paramref name="type"/> was read with <see cref="Options"/> directly.</exception>
        public static Scope Current(Type type) =>
            t_scope ?? throw new InvalidOperationException($"A {type.Name} is read only as a binding's setting, by BindingSettings.Bind.");

        /// <summary>The pointer within the binding of the value at <paramref name="offset"/> of the document.</summary>
        public string PointerAt(long offset) => prefix + BindingSettings.PointerAt(utf8.Span, offset);
    }

    private sealed class LocalFileConverter : JsonConverter<LocalFile>
    {
        public override LocalFile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var scope = Scope.Current(typeof(LocalFile));
            var pointer = scope.PointerAt(reader.TokenStartIndex);
            return scope.Context.LocalFile(JsonElement.ParseValue(ref reader), pointer);
        }

        public override void Write(Utf8JsonWriter writer, LocalFile value, JsonSerializerOptions options) => writer.WriteStringValue(value.Path);
    }

    private sealed class SecretReferenceConverter : JsonConverter<SecretReference>
    {
        public override SecretReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var scope = Scope.Current(typeof(SecretReference));
            var pointer = scope.PointerAt(reader.TokenStartIndex);
            return scope.Context.SecretReference(JsonElement.ParseValue(ref reader), pointer);
        }

        /// <summary>The reference as written (<c>secret://name</c>), never a value.</summary>
        public override void Write(Utf8JsonWriter writer, SecretReference value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// The base of a binding factory: it declares the type of its settings once, as its type argument, and gets them bound to
/// it (<see cref="BindingSettings"/>), so the type the schema describes is the type that binds, and the factory
/// parses no JSON.
/// </summary>
/// <typeparam name="TInstance">The contract the factory's instances serve, e.g. a model client.</typeparam>
/// <typeparam name="TSettings">The settings type: snake_case members, documented for the schema, required ones
/// <c>required</c>; <see cref="LocalFile"/> and <see cref="SecretReference"/> for files and credentials.</typeparam>
public abstract class BindingFactory<TInstance, TSettings> : IBindingFactory<TInstance>
    where TInstance : class
    where TSettings : class
{
    public abstract string Type { get; }

    public Type SettingsType => typeof(TSettings);

    /// <summary>Binds the settings strictly and creates the instance from them.</summary>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    public TInstance Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(BindingSettings.Bind<TSettings>(context, Type), context);
    }

    /// <summary>Creates the instance: checks what the settings type cannot say, and reads nothing else (see <see cref="BindingContext"/>).</summary>
    /// <exception cref="BindingException">A setting is wrong: at its pointer, with a hint.</exception>
    protected abstract TInstance Create(TSettings settings, BindingContext context);
}
