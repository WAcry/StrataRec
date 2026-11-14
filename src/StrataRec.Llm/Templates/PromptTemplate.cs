using System.Buffers;
using System.Globalization;
using System.Text;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Operators;

namespace StrataRec.Llm.Templates;

/// <summary>What a prompt is rendered for, which decides the names its template may use (<see cref="PromptTemplate"/>).</summary>
public enum PromptScope : byte
{
    /// <summary>
    /// Once per call, over a window of rows (<c>LlmReranker@1</c>): <c>{{#rows}}</c> iterates them, with <c>col.*</c> and
    /// <c>{{rank}}</c> inside it.
    /// </summary>
    Window = 0,

    /// <summary>Once per row (<c>LlmAnnotator@1</c>): <c>col.*</c> is that row's value, anywhere; there is no <c>rows</c> or <c>rank</c>.</summary>
    Row,

    /// <summary>Once per request (<c>GenerativeRetriever@1</c>): <c>ctx.*</c> and <c>param.*</c> only, since no row is shown.</summary>
    Request,
}

/// <summary>
/// A prompt template of one node (design 5.9 rule 1): a <see cref="MustacheTemplate"/> whose names are StrataRec
/// references, resolved and type-checked once, when the node is compiled, and rendered per request.
/// </summary>
/// <remarks>
/// <para><b>Names.</b> <c>ctx.*</c> and <c>param.*</c> anywhere. The rest depends on the <see cref="PromptScope"/>: over a
/// window, <c>rows</c> is the section over the window's rows (in rank order), and inside it <c>col.*</c> is the current
/// row's value and <c>rank</c> its 1-based position in the window, what an answer of window ranks refers to; for one row,
/// <c>col.*</c> is that row's value anywhere; for the request, there are no columns. A name only another scope has is an
/// error that says so (<c>MUSTACHE_SCOPE</c>), so a template copied from another operator fails at load, not in the
/// model's answers. Every name is resolved in the node's compile scope, so an unknown one is a load error with a
/// did-you-mean, and recorded as a read of the node, so the fetch planner fetches its features before the node runs.
/// Mustache has no logic: a value the prompt needs computed is a derived feature, one definition for training and
/// serving, not a second expression language.</para>
/// <para><b>Values.</b> Strings and text verbatim; integers in invariant culture; floats in their shortest round-trip
/// form (a <c>float32</c> as float32, so 0.1 renders as <c>0.1</c>); bools as <c>true</c> / <c>false</c>. A list renders
/// only as a section, one item at a time (<c>{{#ctx.tags}}{{.}}, {{/ctx.tags}}</c>). A vector cannot be rendered.</para>
/// <para><b>Sections.</b> Over <c>rows</c> and lists: once per item. Over a bool: once when true. Over a nullable value:
/// once when present, and inside it <c>{{name}}</c> (or <c>{{.}}</c>) renders the value. An inverted section renders when
/// the list is empty, the bool false or the value missing. A section over a value that always has one (a non-nullable
/// string or number) is a load error: it would always render.</para>
/// <para><b>Missing is explicit</b> (AGENTS.md): <c>{{x}}</c> of a nullable reference must be inside a <c>{{#x}}</c>
/// section on the same name, or it is a load error (<c>MUSTACHE_NULLABLE</c>): a missing value is never rendered as empty
/// text silently.</para>
/// <para><b>Errors</b> are reported at the template's config pointer with the character offset of the tag at fault and a
/// hint.</para>
/// <para><b>Cost.</b> Rendering walks the compiled template and reads each value straight from its column or the request:
/// it writes into a builder kept per thread and formats numbers on the stack, so once a thread has rendered, the prompt
/// string it returns is its only allocation. A builder that grew beyond 256 Ki characters is not kept, so a prompt that
/// long also allocates its builder each time: keeping that much memory for every thread's life is the worse cost
/// (review 29).</para>
/// </remarks>
public sealed class PromptTemplate
{
    /// <summary>The section over the window's rows.</summary>
    public const string RowsName = "rows";

    /// <summary>Inside <c>rows</c>: the row's 1-based position in the window.</summary>
    public const string RankName = "rank";

    /// <summary>List sections whose current item fits on the stack while rendering; a template with more rents from the pool.</summary>
    private const int StackFrames = 16;

    private readonly MustacheTemplate _template;
    private readonly Binding[] _bindings;
    private readonly int _frames;

    private PromptTemplate(MustacheTemplate template, Binding[] bindings, int frames, PromptScope scope)
    {
        _template = template;
        _bindings = bindings;
        _frames = frames;
        Scope = scope;
    }

    /// <summary>The template as configured.</summary>
    public string Source => _template.Source;

    /// <summary>What the template is rendered for, which decides the <c>Render</c> overload it takes.</summary>
    public PromptScope Scope { get; }

    /// <summary>
    /// Parses and binds <paramref name="source"/> in the node's scope, recording its references as the node's reads.
    /// Null after reporting every error at <paramref name="configKey"/> (a pointer below the node's <c>config</c>, e.g.
    /// <c>/prompts/user</c>).
    /// </summary>
    /// <param name="scope">What the prompt is rendered for, which decides the names it may use.</param>
    public static PromptTemplate? Compile(OperatorCompileContext context, string? source, string configKey, PromptScope scope = PromptScope.Window)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(configKey);
        if (string.IsNullOrWhiteSpace(source))
        {
            context.Error(configKey, "MUSTACHE_EMPTY", "The template is empty.", "write the prompt as a Mustache template, e.g. " + scope switch
            {
                PromptScope.Row => "\"In one sentence, why would this user like {{col.item.title}}?\"",
                PromptScope.Request => "\"Suggest items for the query '{{ctx.request.query}}' as a JSON array of ids.\"",
                _ => "\"Rank these items:\\n{{#rows}}{{rank}}. {{col.item.title}}\\n{{/rows}}\"",
            });
            return null;
        }

        if (!MustacheTemplate.TryParse(source, out var template, out var error))
        {
            context.Error(configKey, error.Code, At(error.Message, error.Offset), error.Hint);
            return null;
        }

        var binder = new Binder(context, template, configKey, scope);
        binder.Bind(template.Nodes);
        return binder.Failed ? null : new PromptTemplate(template, binder.Bindings, binder.Frames, scope);
    }

    /// <summary>
    /// Renders a <see cref="PromptScope.Window"/> prompt for this request: <paramref name="window"/> are the physical rows
    /// the <c>rows</c> section iterates, in rank order. Synchronous, so the pooled builder never crosses an await.
    /// </summary>
    public string Render(OperatorContext context, ReadOnlySpan<int> window) => Render(context, PromptScope.Window, window, -1);

    /// <summary>Renders a <see cref="PromptScope.Row"/> prompt for physical row <paramref name="row"/>.</summary>
    public string Render(OperatorContext context, int row) => Render(context, PromptScope.Row, default, row);

    /// <summary>Renders a <see cref="PromptScope.Request"/> prompt.</summary>
    public string Render(OperatorContext context) => Render(context, PromptScope.Request, default, -1);

    private string Render(OperatorContext context, PromptScope scope, ReadOnlySpan<int> window, int row)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (scope != Scope)
        {
            throw new InvalidOperationException($"The template was compiled for the {Scope} scope, so it cannot be rendered for the {scope} scope.");
        }

        int[]? rented = null;
        Span<int> frames = _frames <= StackFrames ? stackalloc int[StackFrames] : (rented = ArrayPool<int>.Shared.Rent(_frames));
        var builder = PromptBuilder.Acquire(_template.Source.Length);
        try
        {
            var data = new PromptData(_bindings, context, window, frames, row);
            _template.Render(ref data, builder);
            return builder.ToString();
        }
        finally
        {
            PromptBuilder.Release(builder);
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    private static string At(string message, int offset) => $"{message} (at offset {offset.ToString(CultureInfo.InvariantCulture)} of the template)";

    private enum BindingKind : byte
    {
        /// <summary>The section over the window's rows.</summary>
        Rows = 1,

        /// <summary>The current row's 1-based position in the window.</summary>
        Rank,

        /// <summary>A reference: a variable, or a section over a list, a bool or a nullable value.</summary>
        Value,

        /// <summary><c>{{.}}</c> in a list section: the current item of the list.</summary>
        Item,

        /// <summary><c>{{.}}</c> in a section over a bool or a nullable value: that value.</summary>
        SectionValue,
    }

    /// <summary>A resolved <c>col.*</c>, <c>ctx.*</c> or <c>param.*</c> name, ready to read per request.</summary>
    /// <param name="float32">
    /// Declared float32, so rendered as float32: a request input's symbol has the promoted type (float64), but its value
    /// has float32 precision, and its float64 digits would only show the rounding (0.1 as 0.10000000149011612).
    /// </param>
    private readonly struct Reference(in Symbol symbol, bool float32)
    {
        public Symbol Symbol { get; } = symbol;

        public bool Float32 { get; } = float32;

        public ColumnRef Column { get; } = new(symbol.Id, symbol.FixedSlot, symbol.Type, symbol.Name);

        public ParamRef Param { get; } = new(symbol.Id, symbol.Name, symbol.Type);

        public ColumnType Type => Symbol.Type;

        public bool IsList => Type.Kind is StorageKind.ListInt64 or StorageKind.ListInt32 or StorageKind.ListString;
    }

    /// <summary>What a tag does per request, by <see cref="MustacheTag.Id"/>.</summary>
    /// <param name="Frame">For a list section and the <c>{{.}}</c> inside it: the slot holding the current item's index.</param>
    private readonly record struct Binding(BindingKind Kind, Reference Reference = default, int Frame = -1);

    /// <summary>Resolves and checks every tag once, at compile time.</summary>
    private sealed class Binder(OperatorCompileContext context, MustacheTemplate template, string configKey, PromptScope scope)
    {
        private readonly List<(MustacheTag Tag, Binding Binding)> _open = new();

        public Binding[] Bindings { get; } = new Binding[template.TagCount];

        /// <summary>The number of list sections, each with a slot for its current item.</summary>
        public int Frames { get; private set; }

        public bool Failed { get; private set; }

        public void Bind(IReadOnlyList<MustacheNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is not MustacheTag tag)
                {
                    continue;
                }

                if (tag.Kind == MustacheTagKind.Variable)
                {
                    BindVariable(tag);
                }
                else
                {
                    BindSection(tag);
                }
            }
        }

        private void BindVariable(MustacheTag tag)
        {
            var name = tag.Name;
            switch (name)
            {
                case ".":
                    BindImplicit(tag);
                    return;
                case RowsName or RankName when scope != PromptScope.Window:
                    ScopeError(tag);
                    return;
                case RowsName:
                    Error(tag, "MUSTACHE_ROWS", $"{Text(tag)}: 'rows' is the section over the window's rows, not a value.",
                        "write {{#rows}}…{{/rows}} and name each row's values inside it, e.g. {{#rows}}{{rank}}. {{col.item.title}}\\n{{/rows}}");
                    return;
                case RankName:
                    if (!InRows())
                    {
                        Error(tag, "MUSTACHE_ROW_OUTSIDE_ROWS", $"{Text(tag)}: 'rank' is a row's position in the window, so it has a value only inside {{{{#rows}}}}.",
                            "move it inside {{#rows}}…{{/rows}}");
                        return;
                    }

                    Bindings[tag.Id] = new Binding(BindingKind.Rank);
                    return;
            }

            if (!TryResolve(tag, out var reference))
            {
                return;
            }

            var type = reference.Type;
            if (type.Kind == StorageKind.Vector)
            {
                Error(tag, "MUSTACHE_UNRENDERABLE", $"{Text(tag)}: '{name}' is {type}, and a vector cannot be rendered as text.",
                    "render a derived feature computed from it (one definition for training and serving), or leave it out of the prompt");
                return;
            }

            if (reference.IsList)
            {
                Error(tag, "MUSTACHE_UNRENDERABLE", $"{Text(tag)}: '{name}' is {type}, and a list renders only as a section, one item at a time.",
                    $"iterate it: {{{{#{name}}}}}{{{{.}}}}, {{{{/{name}}}}}");
                return;
            }

            if (type.Nullable && !Guarded(name))
            {
                Error(tag, "MUSTACHE_NULLABLE",
                    $"{Text(tag)}: '{name}' is {type} and may be missing; outside a {{{{#{name}}}}} section a missing value would render as nothing, silently.",
                    $"guard it: {{{{#{name}}}}}{{{{{name}}}}}{{{{/{name}}}}}{{{{^{name}}}}}unknown{{{{/{name}}}}}, or render a non-nullable derived feature "
                    + "that settles the missing case with '??'");
                return;
            }

            Bindings[tag.Id] = new Binding(BindingKind.Value, reference);
        }

        /// <summary><c>{{.}}</c>: the item or value of the innermost (non-inverted) section around it.</summary>
        private void BindImplicit(MustacheTag tag)
        {
            for (var i = _open.Count - 1; i >= 0; i--)
            {
                var (section, binding) = _open[i];
                if (section.Kind != MustacheTagKind.Section)
                {
                    continue; // an inverted section renders when there is no value: it has none to give
                }

                if (binding.Kind == BindingKind.Rows)
                {
                    Error(tag, "MUSTACHE_IMPLICIT", $"{Text(tag)} inside {{{{#rows}}}} would be the whole row, which has no text form.",
                        "name the value to show, e.g. {{col.item.title}}, or {{rank}} for the row's position");
                    return;
                }

                Bindings[tag.Id] = binding.Reference.IsList
                    ? new Binding(BindingKind.Item, binding.Reference, binding.Frame)
                    : new Binding(BindingKind.SectionValue, binding.Reference);
                return;
            }

            Error(tag, "MUSTACHE_IMPLICIT", $"{Text(tag)} is outside every section, so there is no current item to render.",
                "use {{.}} inside a section over a list, e.g. {{#ctx.tags}}{{.}}, {{/ctx.tags}}");
        }

        private void BindSection(MustacheTag tag)
        {
            var name = tag.Name;
            var inverted = tag.Kind == MustacheTagKind.InvertedSection;
            Binding binding;
            switch (name)
            {
                case ".":
                    Error(tag, "MUSTACHE_SECTION_TYPE", $"{Text(tag)} would iterate the current item, but no StrataRec value is a list of lists.",
                        "iterate a list by its name, e.g. {{#ctx.tags}}{{.}}, {{/ctx.tags}}");
                    return;
                case RowsName or RankName when scope != PromptScope.Window:
                    ScopeError(tag);
                    return;
                case RowsName when inverted:
                    Error(tag, "MUSTACHE_SECTION_TYPE", $"{Text(tag)} would render only for an empty window, but the node calls no model then.",
                        "remove the inverted section");
                    return;
                case RowsName when InRows():
                    Error(tag, "MUSTACHE_ROWS", $"{Text(tag)} is inside another {{{{#rows}}}} section: rows do not nest.",
                        "close the outer section with {{/rows}} before opening another");
                    return;
                case RowsName:
                    binding = new Binding(BindingKind.Rows);
                    break;
                case RankName:
                    Error(tag, "MUSTACHE_SECTION_TYPE", $"{Text(tag)}: 'rank' always has a value, so a section over it would always render.",
                        "write {{rank}} to show the row's position");
                    return;
                default:
                    if (!TryResolve(tag, out var reference))
                    {
                        return;
                    }

                    var type = reference.Type;
                    if (type.Kind == StorageKind.Vector)
                    {
                        Error(tag, "MUSTACHE_UNRENDERABLE", $"{Text(tag)}: '{name}' is {type}, and a vector cannot be rendered or iterated.",
                            "render a derived feature computed from it, or leave it out of the prompt");
                        return;
                    }

                    if (!reference.IsList && type.Kind != StorageKind.Bool && !type.Nullable)
                    {
                        Error(tag, "MUSTACHE_SECTION_TYPE",
                            $"{Text(tag)}: '{name}' is {type}, which always has a value, so the section would {(inverted ? "never" : "always")} render.",
                            $"a section takes a list (once per item), a bool (when true) or a nullable value (when present); show the value with {{{{{name}}}}}");
                        return;
                    }

                    binding = new Binding(BindingKind.Value, reference, reference.IsList && !inverted ? Frames++ : -1);
                    break;
            }

            Bindings[tag.Id] = binding;
            _open.Add((tag, binding));
            Bind(tag.Children);
            _open.RemoveAt(_open.Count - 1);
        }

        private bool TryResolve(MustacheTag tag, out Reference reference)
        {
            reference = default;
            var name = tag.Name;
            var column = name.StartsWith("col.", StringComparison.Ordinal);
            if (column && scope == PromptScope.Request)
            {
                ScopeError(tag);
                return false;
            }

            var names = context.Scope;
            if (!(column || name.StartsWith("ctx.", StringComparison.Ordinal) || name.StartsWith("param.", StringComparison.Ordinal))
                || !names.TryResolve(name, out var symbol))
            {
                var known = new List<string>();
                foreach (var candidate in names.KnownNames)
                {
                    if (scope != PromptScope.Request || !candidate.StartsWith("col.", StringComparison.Ordinal))
                    {
                        known.Add(candidate);
                    }
                }

                if (scope == PromptScope.Window)
                {
                    known.Add(RowsName);
                    known.Add(RankName);
                }

                Error(tag, "MUSTACHE_UNKNOWN_NAME", $"{Text(tag)}: unknown name '{name}'.", NameSuggestions.DidYouMean(name, known) ?? scope switch
                {
                    PromptScope.Row => "a per-row template names col.* (the row's values), ctx.* and param.* references",
                    PromptScope.Request => "a per-request template names ctx.* and param.* references",
                    _ => "a template names col.* (inside {{#rows}}), ctx.* and param.* references, rows and rank",
                });
                return false;
            }

            if (symbol.Kind == SymbolKind.Column && scope == PromptScope.Window && !InRows())
            {
                Error(tag, "MUSTACHE_ROW_OUTSIDE_ROWS", $"{Text(tag)}: '{name}' is a column, which has a value per row, so it renders only inside {{{{#rows}}}}.",
                    "move it inside {{#rows}}…{{/rows}}, or render a ctx.* value");
                return false;
            }

            context.RecordSymbol(symbol);
            var float32 = symbol.Type.Kind == StorageKind.Float32
                          || (symbol.Kind == SymbolKind.Context && context.Schema.TryGetContext(name["ctx.".Length..], out var field)
                              && field.InputType.Kind == StorageKind.Float32);
            reference = new Reference(symbol, float32);
            return true;
        }

        private bool InRows()
        {
            foreach (var (_, binding) in _open)
            {
                if (binding.Kind == BindingKind.Rows)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Inside a (non-inverted) section on the same name, which renders only when the value is present.</summary>
        private bool Guarded(string name)
        {
            foreach (var (tag, _) in _open)
            {
                if (tag.Kind == MustacheTagKind.Section && string.Equals(tag.Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A name only another scope has: <c>rows</c> and <c>rank</c> outside a window, <c>col.*</c> in a per-request prompt.</summary>
        private void ScopeError(MustacheTag tag)
        {
            var op = context.Descriptor.Id;
            var name = tag.Name;
            if (scope == PromptScope.Row)
            {
                if (name == RowsName)
                {
                    Error(tag, "MUSTACHE_SCOPE", $"{Text(tag)}: {op} renders this prompt once per row, so there is no 'rows' section.",
                        "name the row's values directly, e.g. {{col.item.title}}");
                }
                else
                {
                    Error(tag, "MUSTACHE_SCOPE", $"{Text(tag)}: 'rank' is a row's position in a window, but {op} renders this prompt for one row, with no window.",
                        "leave it out: the prompt shows one row");
                }

                return;
            }

            Error(tag, "MUSTACHE_SCOPE", name.StartsWith("col.", StringComparison.Ordinal)
                    ? $"{Text(tag)}: '{name}' is a column, which has a value per row, but {op} renders this prompt once per request, for no row."
                    : $"{Text(tag)}: {op} renders this prompt once per request, for no rows, so there is no '{name}'.",
                "render request values only: ctx.* and param.*");
        }

        private string Text(MustacheTag tag) => template.Source.Substring(tag.Offset, tag.Length);

        private void Error(MustacheTag tag, string code, string message, string hint)
        {
            Failed = true;
            context.Error(configKey, code, At(message, tag.Offset), hint);
        }
    }

    /// <summary>The values a request renders: the window, the current row, and the current item of each list section.</summary>
    /// <param name="row">The row a per-row prompt shows; otherwise -1, until a <c>rows</c> section enters one.</param>
    private ref struct PromptData(Binding[] bindings, OperatorContext context, ReadOnlySpan<int> window, Span<int> frames, int row) : IMustacheData
    {
        private readonly ReadOnlySpan<int> _window = window;
        private readonly Span<int> _frames = frames;
        private int _position = -1;
        private int _row = row;

        public readonly void Write(MustacheTag variable, StringBuilder output)
        {
            var binding = bindings[variable.Id];
            switch (binding.Kind)
            {
                case BindingKind.Rank:
                    PromptValues.Append(output, _position + 1L);
                    break;
                case BindingKind.Value or BindingKind.SectionValue:
                    WriteValue(binding.Reference, output);
                    break;
                case BindingKind.Item:
                    WriteItem(binding.Reference, _frames[binding.Frame], output);
                    break;
                default:
                    throw new InvalidOperationException($"Tag {variable.Id} ('{variable.Name}') is not a value.");
            }
        }

        public readonly int Count(MustacheTag section)
        {
            var binding = bindings[section.Id];
            return binding.Kind == BindingKind.Rows ? _window.Length
                : binding.Reference.IsList ? ListLength(binding.Reference)
                : Truthy(binding.Reference) ? 1 : 0;
        }

        public void Enter(MustacheTag section, int index)
        {
            var binding = bindings[section.Id];
            if (binding.Kind == BindingKind.Rows)
            {
                _position = index;
                _row = _window[index];
            }
            else if (binding.Frame >= 0)
            {
                _frames[binding.Frame] = index;
            }
        }

        public void Leave(MustacheTag section)
        {
            if (bindings[section.Id].Kind == BindingKind.Rows)
            {
                _position = -1;
                _row = -1;
            }
        }

        /// <summary>
        /// The column's buffer: null for a nullable column nothing wrote in this request (every row missing). A non-nullable
        /// one always has data; its validity is not read, as the expression evaluator does not read it.
        /// </summary>
        private readonly ColumnBuffer? Column(in Reference reference) =>
            context.Read(reference.Column) is { } buffer ? buffer
            : reference.Type.Nullable ? null
            : throw new InvalidOperationException($"Column '{reference.Symbol.Name}' is declared non-nullable ({reference.Type}) but has no data in this request.");

        private readonly bool Present(in Reference reference, out ColumnBuffer? buffer)
        {
            buffer = Column(reference);
            return buffer is not null && (!reference.Type.Nullable || buffer.IsValid(_row));
        }

        private readonly ScalarValue Scalar(in Reference reference) =>
            reference.Symbol.Kind == SymbolKind.Context ? context.ContextValue(reference.Symbol.Id) : context.Param(reference.Param);

        /// <summary>Present and, for a bool, true.</summary>
        private readonly bool Truthy(in Reference reference)
        {
            if (reference.Symbol.Kind == SymbolKind.Column)
            {
                return Present(reference, out var buffer) && (buffer is not ScalarColumn<bool> flags || flags[_row]);
            }

            var value = Scalar(reference);
            return !value.IsMissing && (value.Kind != StorageKind.Bool || value.AsBool());
        }

        private readonly int ListLength(in Reference reference)
        {
            if (reference.Symbol.Kind == SymbolKind.Column)
            {
                return !Present(reference, out var buffer) ? 0 : buffer switch
                {
                    ListColumn<long> longs => longs.LengthOf(_row),
                    ListColumn<int> ints => ints.LengthOf(_row),
                    ListColumn<string> strings => strings.LengthOf(_row),
                    _ => throw Unexpected(reference, buffer),
                };
            }

            var value = Scalar(reference);
            return value.Kind switch
            {
                0 => 0,
                StorageKind.ListInt64 => value.AsInt64List().Length,
                StorageKind.ListString => value.AsStringList().Length,
                _ => throw Unexpected(reference, value.Kind),
            };
        }

        private readonly void WriteItem(in Reference reference, int index, StringBuilder output)
        {
            if (reference.Symbol.Kind == SymbolKind.Column)
            {
                switch (Column(reference))
                {
                    case ListColumn<long> longs:
                        PromptValues.Append(output, longs.Get(_row)[index]);
                        return;
                    case ListColumn<int> ints:
                        PromptValues.Append(output, ints.Get(_row)[index]);
                        return;
                    case ListColumn<string> strings:
                        output.Append(strings.Get(_row)[index]);
                        return;
                    case var other:
                        throw Unexpected(reference, other);
                }
            }

            var value = Scalar(reference);
            switch (value.Kind)
            {
                case StorageKind.ListInt64:
                    PromptValues.Append(output, value.AsInt64List()[index]);
                    break;
                case StorageKind.ListString:
                    output.Append(value.AsStringList()[index]);
                    break;
                default:
                    throw Unexpected(reference, value.Kind);
            }
        }

        private readonly void WriteValue(in Reference reference, StringBuilder output)
        {
            if (reference.Symbol.Kind == SymbolKind.Column)
            {
                switch (Column(reference))
                {
                    case ScalarColumn<bool> bools:
                        PromptValues.Append(output, bools[_row]);
                        return;
                    case ScalarColumn<int> ints:
                        PromptValues.Append(output, ints[_row]);
                        return;
                    case ScalarColumn<long> longs:
                        PromptValues.Append(output, longs[_row]);
                        return;
                    case ScalarColumn<float> floats:
                        PromptValues.Append(output, floats[_row]);
                        return;
                    case ScalarColumn<double> doubles:
                        PromptValues.Append(output, doubles[_row]);
                        return;
                    case DictStringColumn strings:
                        output.Append(strings.Get(_row));
                        return;
                    case TextColumn texts:
                        output.Append(texts.Get(_row));
                        return;
                    case var other:
                        throw Unexpected(reference, other);
                }
            }

            // Request-level values are stored promoted: a float32 one is a float64 holding a float32's value.
            var value = Scalar(reference);
            switch (value.Kind)
            {
                case StorageKind.Bool:
                    PromptValues.Append(output, value.AsBool());
                    break;
                case StorageKind.Int64:
                    PromptValues.Append(output, value.AsInt64());
                    break;
                case StorageKind.Float64 when reference.Float32:
                    PromptValues.Append(output, (float)value.AsFloat64());
                    break;
                case StorageKind.Float64:
                    PromptValues.Append(output, value.AsFloat64());
                    break;
                case StorageKind.String or StorageKind.Text:
                    output.Append(value.AsString());
                    break;
                default:
                    throw Unexpected(reference, value.Kind);
            }
        }

        private static InvalidOperationException Unexpected(in Reference reference, object? what) =>
            new($"'{reference.Symbol.Name}' ({reference.Type}) holds {what?.ToString() ?? "nothing"} in this request, which the template's checks exclude.");
    }
}

/// <summary>Formats prompt values without allocating: on the stack, in invariant culture.</summary>
internal static class PromptValues
{
    public static void Append(StringBuilder output, bool value) => output.Append(value ? "true" : "false");

    public static void Append(StringBuilder output, long value)
    {
        Span<char> buffer = stackalloc char[24];
        value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
        output.Append(buffer[..written]);
    }

    /// <summary>The shortest text that reads back as the same float32.</summary>
    public static void Append(StringBuilder output, float value)
    {
        Span<char> buffer = stackalloc char[32];
        value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
        output.Append(buffer[..written]);
    }

    /// <summary>The shortest text that reads back as the same float64.</summary>
    public static void Append(StringBuilder output, double value)
    {
        Span<char> buffer = stackalloc char[32];
        value.TryFormat(buffer, out var written, default, CultureInfo.InvariantCulture);
        output.Append(buffer[..written]);
    }
}

/// <summary>
/// One builder per thread for rendering prompts: after the first renders it has grown to the prompts' size, so a render
/// appends without allocating and only the result string is new. Rendering is synchronous, so a builder never crosses an
/// await; one that grew beyond <see cref="MaxCachedCapacity"/> characters is dropped rather than kept for the thread's life.
/// </summary>
internal static class PromptBuilder
{
    private const int MaxCachedCapacity = 256 * 1024;

    [ThreadStatic]
    private static StringBuilder? t_cached;

    public static StringBuilder Acquire(int sourceLength)
    {
        if (t_cached is { } cached)
        {
            t_cached = null;
            return cached;
        }

        return new StringBuilder(Math.Max(256, 2 * sourceLength));
    }

    public static void Release(StringBuilder builder)
    {
        if (builder.Capacity <= MaxCachedCapacity)
        {
            builder.Clear();
            t_cached = builder;
        }
    }
}
