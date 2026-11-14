using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;

namespace StrataRec.Pipeline.Planning;

/// <summary>
/// The inputs a pipeline accepts (its <c>request_schema</c>, design 3.4) and the one set of rules every request's inputs
/// follow, wherever they come from: a host reads a JSON body with <see cref="TryRead"/>, the engine checks the
/// <see cref="RequestInputs"/> a host built itself with <see cref="Check"/> before it runs anything, and the executor binds
/// them with the same rules. So a mistake reads alike, at the same pointer, from a service, the CLI and the engine.
/// </summary>
/// <remarks>
/// <para>The rules: every input given is declared; a value has its declared type (an int32 must fit in int32, a vector its
/// dimension); a missing value (JSON <c>null</c>, or absent) is allowed only for an input declared nullable (<c>T?</c>).
/// Nothing is ever turned into a default: a missing input stays missing, for expressions to handle (<c>has()</c>, <c>??</c>).</para>
/// <para>Errors are in the words of the JSON (<c>Input 'user_id' must be an integer (int64), got the string "x".</c>) and
/// located at <c>/inputs/&lt;name&gt;</c>, a pointer relative to a request body of the shape <c>{ "inputs": { … } }</c>; a
/// host whose body puts the inputs elsewhere rebases it.</para>
/// </remarks>
public sealed class RequestSchema
{
    private readonly Dictionary<string, int> _index;
    private readonly string[] _names; // sorted (ordinal); the arrays below are in the same order
    private readonly ColumnType[] _types; // as declared, e.g. int32? (values are held promoted)
    private readonly int[] _contextIds; // the plan's context slot of each input; -1 for a schema not built with a plan
    private readonly byte[][] _utf8Names; // to match a JSON member without allocating its name

    /// <summary>A schema of declared types, e.g. for a host that checks inputs against a schema it keeps itself.</summary>
    /// <param name="pipelineId">Named in messages: <c>not declared in the request_schema of pipeline 'p'</c>.</param>
    /// <param name="inputs">Input name → declared type (<c>int64</c>, <c>string?</c>, <c>list&lt;int32&gt;</c>, …).</param>
    public RequestSchema(string pipelineId, IReadOnlyDictionary<string, ColumnType> inputs)
        : this(pipelineId, Sorted(inputs.Keys), name => (inputs[name], -1))
    {
    }

    private RequestSchema(string pipelineId, string[] names, Func<string, (ColumnType Type, int ContextId)> input)
    {
        PipelineId = pipelineId;
        _names = names;
        _types = new ColumnType[names.Length];
        _contextIds = new int[names.Length];
        _utf8Names = new byte[names.Length][];
        _index = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
        {
            (_types[i], _contextIds[i]) = input(names[i]);
            _utf8Names[i] = Encoding.UTF8.GetBytes(names[i]);
            _index[names[i]] = i;
        }
    }

    /// <summary>The schema of a compiled plan: each input bound to its context slot.</summary>
    internal static RequestSchema FromPlan(string pipelineId, IReadOnlyDictionary<string, ContextField> fields) =>
        new(pipelineId, Sorted(fields.Keys), name => (fields[name].InputType, fields[name].Id));

    public string PipelineId { get; }

    public int Count => _names.Length;

    /// <summary>The declared inputs, sorted (ordinal).</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>The type an input is declared with (e.g. <c>int32?</c>); values are held in its promoted form.</summary>
    public bool TryGetType(string name, out ColumnType declared)
    {
        if (_index.TryGetValue(name, out var i))
        {
            declared = _types[i];
            return true;
        }

        declared = default;
        return false;
    }

    public bool Contains(string name) => _index.ContainsKey(name);

    /// <summary>
    /// Warnings for a fallback pipeline that would fail the requests it is there to serve (design 5.6): a request that
    /// falls back carries the inputs it was written for, those of <paramref name="primary"/>, and the fallback gets the ones
    /// it declares. So the fallback fails it when it requires an input the primary does not declare, or declares optional;
    /// and may fail it when an input is declared with another type. Reported at the primary's <c>/fallback_pipeline</c>.
    /// </summary>
    public static void CheckFallback(RequestSchema primary, RequestSchema fallback, string artifact, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(diagnostics);
        const string Pointer = "/fallback_pipeline";
        var (p, f) = (primary.PipelineId, fallback.PipelineId);
        foreach (var name in fallback.Names)
        {
            fallback.TryGetType(name, out var required);
            if (!primary.TryGetType(name, out var given))
            {
                if (!required.Nullable)
                {
                    diagnostics.Warning("FALLBACK_INPUT_UNDECLARED", artifact, Pointer,
                        $"Fallback pipeline '{f}' requires input '{name}' ({required}), which '{p}' does not declare: every request that falls back to it fails.",
                        $"declare '{name}' in the request_schema of '{p}', or make it optional in '{f}' ('{required.AsNullable()}')");
                }

                continue;
            }

            if (!given.AsNonNullable().Equals(required.AsNonNullable()))
            {
                diagnostics.Warning("FALLBACK_INPUT_TYPE", artifact, Pointer,
                    $"Input '{name}' is {given} in '{p}' but {required} in its fallback pipeline '{f}': a value the first accepts may not convert, and the request that falls back fails.",
                    "declare the input with the same type in both request schemas");
            }
            else if (given.Nullable && !required.Nullable)
            {
                diagnostics.Warning("FALLBACK_INPUT_OPTIONAL", artifact, Pointer,
                    $"Input '{name}' is optional in '{p}' ({given}) but required in its fallback pipeline '{f}' ({required}): a request without it fails when it falls back.",
                    $"make '{name}' optional in '{f}' too, or required in '{p}'");
            }
        }
    }

    /// <summary>Where an input is in a request body: <c>/inputs/&lt;name&gt;</c> (RFC 6901, escaped).</summary>
    public static string Pointer(string name) => "/inputs/" + JsonPointer.Escape(name);

    /// <summary>
    /// An error as one line of text, its hint a sentence after the message (<c>Input 'contry' is not declared in the
    /// request_schema of pipeline 'p'. Did you mean 'country'?</c>): the text of a result the engine rejected.
    /// </summary>
    public static string Describe(JsonBindingError error)
    {
        if (string.IsNullOrEmpty(error.Hint))
        {
            return error.Message;
        }

        var hint = char.ToUpperInvariant(error.Hint[0]) + error.Hint[1..];
        return error.Message + " " + hint + (hint[^1] is '.' or '?' or '!' ? string.Empty : ".");
    }

    /// <summary>
    /// Reads the <c>inputs</c> object of a request body. Every problem is reported, each at its pointer: undeclared inputs
    /// (with a "did you mean" or the declared names), values of the wrong type, and required inputs that are null or
    /// absent. JSON <c>null</c> and an absent <paramref name="inputs"/> are no inputs. What System.Text.Json parses but a
    /// request must not hold (<see cref="StrictJson"/>: an input given twice, an escape that is not Unicode) is the one error
    /// reported then, at its pointer.
    /// </summary>
    /// <remarks>Allocates the result and the values themselves (strings, arrays), nothing per input besides; errors only on
    /// the error path. No input makes it throw.</remarks>
    public bool TryRead(in JsonElement inputs, [NotNullWhen(true)] out RequestInputs? values, out IReadOnlyList<JsonBindingError> errors)
    {
        List<JsonBindingError>? problems = null;
        var read = new RequestInputs(_names.Length);
        Span<bool> given = _names.Length <= 64 ? stackalloc bool[64] : new bool[_names.Length];
        given = given[.._names.Length];
        given.Clear();
        switch (inputs.ValueKind)
        {
            case JsonValueKind.Object:
                if (StrictJson.Check(inputs) is { } strict)
                {
                    values = null;
                    errors = new[] { strict with { Pointer = "/inputs" + strict.Pointer } };
                    return false;
                }

                foreach (var member in inputs.EnumerateObject())
                {
                    var i = IndexOf(member);
                    if (i < 0)
                    {
                        (problems ??= new()).Add(Undeclared(member.Name));
                        continue;
                    }

                    given[i] = true;
                    if (member.Value.ValueKind == JsonValueKind.Null)
                    {
                        if (!_types[i].Nullable)
                        {
                            (problems ??= new()).Add(Required(i));
                        }

                        continue; // missing, as if absent
                    }

                    if (!ConfigValues.TryToScalar(member.Value, _types[i], out var value, out var error))
                    {
                        (problems ??= new()).Add(Invalid(i, error));
                        continue;
                    }

                    read.Set(_names[i], value);
                }

                break;
            case JsonValueKind.Undefined or JsonValueKind.Null:
                break;
            default:
                values = null;
                errors = new[]
                {
                    new JsonBindingError("/inputs", $"'inputs' must be an object of input values by name, got {JsonWords.Value(inputs)}.", DeclaredHint()),
                };
                return false;
        }

        for (var i = 0; i < _names.Length; i++)
        {
            if (!given[i] && !_types[i].Nullable)
            {
                (problems ??= new()).Add(Required(i));
            }
        }

        values = problems is null ? read : null;
        errors = problems ?? (IReadOnlyList<JsonBindingError>)Array.Empty<JsonBindingError>();
        return problems is null;
    }

    /// <summary>
    /// Checks typed inputs a host built itself: the first problem, or null when the request may run. The engine calls it
    /// before anything else (no assignment, no fallback pipeline for a request the caller got wrong); it allocates nothing
    /// when the inputs are valid.
    /// </summary>
    public JsonBindingError? Check(RequestInputs inputs) => Validate(inputs, null);

    /// <summary>
    /// <see cref="Check(RequestInputs)"/>, and each declared input's value, converted to its promoted form (or missing),
    /// written to its context slot. Only for the schema of the plan that owns <paramref name="context"/>.
    /// </summary>
    internal JsonBindingError? Bind(RequestInputs inputs, ScalarValue[] context) => Validate(inputs, context);

    private JsonBindingError? Validate(RequestInputs inputs, ScalarValue[]? context)
    {
        foreach (var (name, _) in inputs)
        {
            if (!_index.ContainsKey(name))
            {
                return Undeclared(name);
            }
        }

        for (var i = 0; i < _names.Length; i++)
        {
            inputs.TryGetValue(_names[i], out var value);
            if (value.IsMissing)
            {
                if (!_types[i].Nullable)
                {
                    return Required(i);
                }
            }
            else if (ConfigValues.TryConvert(value, _types[i], out var converted, out var error))
            {
                value = converted;
            }
            else
            {
                return Invalid(i, error);
            }

            if (context is not null)
            {
                context[_contextIds[i]] = value;
            }
        }

        return null;
    }

    /// <summary>The declared input a JSON member names, compared in UTF-8 (escapes included) without allocating its name.</summary>
    private int IndexOf(in JsonProperty member)
    {
        for (var i = 0; i < _utf8Names.Length; i++)
        {
            if (member.NameEquals(_utf8Names[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private JsonBindingError Undeclared(string name) =>
        new(Pointer(name), $"Input '{name}' is not declared in the request_schema of pipeline '{PipelineId}'.",
            Artifacts.Model.NameSuggestions.Closest(name, _names) is { } close ? $"did you mean '{close}'?" : DeclaredHint());

    private JsonBindingError Required(int i) =>
        new(Pointer(_names[i]), $"Input '{_names[i]}' is required by the request_schema of pipeline '{PipelineId}'.",
            $"give it {ConfigValues.Describe(_types[i])}; only an input declared nullable ('{_types[i].AsNullable()}') may be null or absent");

    private JsonBindingError Invalid(int i, string error) => new(Pointer(_names[i]), $"Input '{_names[i]}' {error}.", null);

    private string DeclaredHint() => _names.Length > 0 ? "declared inputs: " + JsonWords.List(_names) : "the pipeline declares no inputs";

    private static string[] Sorted(IEnumerable<string> names)
    {
        var sorted = new List<string>(names);
        sorted.Sort(StringComparer.Ordinal);
        return sorted.ToArray();
    }
}
