using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace StrataRec.Core.Values;

/// <summary>
/// Typed request inputs supplied by the host (design 3.4), validated against the pipeline's <c>request_schema</c>.
/// Appear in expressions as <c>ctx.request.&lt;name&gt;</c>.
/// </summary>
public sealed class RequestInputs : IReadOnlyDictionary<string, ScalarValue>
{
    private readonly Dictionary<string, ScalarValue> _values = new(StringComparer.Ordinal);

    /// <summary>Creates an empty, mutable instance.</summary>
    public static RequestInputs Empty => new();

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<ScalarValue> Values => _values.Values;

    public ScalarValue this[string key] => _values.TryGetValue(key, out var v) ? v : ScalarValue.Missing;

    public RequestInputs Set(string name, ScalarValue value)
    {
        _values[name] = value;
        return this;
    }

    public RequestInputs Set(string name, long value) => Set(name, ScalarValue.FromInt64(value));

    public RequestInputs Set(string name, double value) => Set(name, ScalarValue.FromFloat64(value));

    public RequestInputs Set(string name, bool value) => Set(name, ScalarValue.FromBool(value));

    public RequestInputs Set(string name, string? value) => Set(name, ScalarValue.FromString(value));

    public RequestInputs Set(string name, long[]? value) => Set(name, ScalarValue.FromInt64List(value));

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out ScalarValue value) => _values.TryGetValue(key, out value);

    /// <summary>A struct enumerator: the engine reads the inputs on every request, and an interface enumerator is an object.</summary>
    public Dictionary<string, ScalarValue>.Enumerator GetEnumerator() => _values.GetEnumerator();

    IEnumerator<KeyValuePair<string, ScalarValue>> IEnumerable<KeyValuePair<string, ScalarValue>>.GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
}
