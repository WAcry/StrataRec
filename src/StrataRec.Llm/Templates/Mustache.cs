using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace StrataRec.Llm.Templates;

/// <summary>A piece of a parsed Mustache template: literal text or a tag.</summary>
internal abstract class MustacheNode
{
    private protected MustacheNode(int offset, int length)
    {
        Offset = offset;
        Length = length;
    }

    /// <summary>Where it starts in the template: a text's first character, or a tag's opening <c>{{</c>.</summary>
    public int Offset { get; }

    /// <summary>A text's length, or the length of a tag as written (of its opening tag, for a section).</summary>
    public int Length { get; }
}

/// <summary>Literal text, rendered as written: <see cref="MustacheNode.Length"/> characters of the template from <see cref="MustacheNode.Offset"/>.</summary>
internal sealed class MustacheText : MustacheNode
{
    internal MustacheText(int offset, int length)
        : base(offset, length)
    {
    }
}

/// <summary>What a tag does.</summary>
internal enum MustacheTagKind : byte
{
    /// <summary><c>{{name}}</c>, <c>{{{name}}}</c> or <c>{{&amp; name}}</c>: the value.</summary>
    Variable = 1,

    /// <summary><c>{{#name}}…{{/name}}</c>: the content once per item of a list, once for a truthy value, never otherwise.</summary>
    Section,

    /// <summary><c>{{^name}}…{{/name}}</c>: the content once when the value is empty, false or missing.</summary>
    InvertedSection,
}

/// <summary>A variable or a section of a parsed template.</summary>
internal sealed class MustacheTag : MustacheNode
{
    private static readonly MustacheNode[] NoChildren = [];

    internal MustacheTag(MustacheTagKind kind, string name, int offset, int length, int id, bool unescaped, MustacheNode[]? children)
        : base(offset, length)
    {
        Kind = kind;
        Name = name;
        Id = id;
        Unescaped = unescaped;
        ChildNodes = children ?? NoChildren;
    }

    public MustacheTagKind Kind { get; }

    /// <summary>The name as written, without the padding around it; <c>.</c> is the implicit iterator.</summary>
    public string Name { get; }

    /// <summary>
    /// The tag's index among the template's variables and sections, in the order they open in the source (0 to
    /// <see cref="MustacheTemplate.TagCount"/> − 1): a binding resolves each tag once and finds it again by this index.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// Written as <c>{{{name}}}</c> or <c>{{&amp; name}}</c>. Rendered the same as <c>{{name}}</c>: this renderer never
    /// escapes (see <see cref="MustacheTemplate"/>).
    /// </summary>
    public bool Unescaped { get; }

    /// <summary>A section's content; empty for a variable.</summary>
    public IReadOnlyList<MustacheNode> Children => ChildNodes;

    internal MustacheNode[] ChildNodes { get; }
}

/// <summary>A template the parser rejects: where (the offset of the tag at fault), why, and how to fix it.</summary>
internal readonly record struct MustacheError(int Offset, string Code, string Message, string Hint);

/// <summary>
/// What a template renders: the values of its tags. The renderer walks the template and asks for each tag; the
/// implementation keeps whatever context its names need (a context stack, the current row).
/// </summary>
/// <remarks>A struct implementation (a <c>ref struct</c> included) is called without boxing or allocation.</remarks>
internal interface IMustacheData
{
    /// <summary>Appends the value of a <see cref="MustacheTagKind.Variable"/> tag.</summary>
    void Write(MustacheTag variable, StringBuilder output);

    /// <summary>
    /// How many times a section renders its content: a list's length, 1 for a truthy value, 0 for an empty list, false or
    /// a missing value. An inverted section renders its content once when this is 0.
    /// </summary>
    int Count(MustacheTag section);

    /// <summary>Before rendering item <paramref name="index"/> of a (non-inverted) section's content.</summary>
    void Enter(MustacheTag section, int index);

    /// <summary>After rendering an item of a section's content.</summary>
    void Leave(MustacheTag section);
}

/// <summary>
/// A Mustache template (the mustache(5) manual and the Mustache specification): parsed once, rendered any number of times
/// through an <see cref="IMustacheData"/>. Independent of StrataRec's types; prompt templates bind it to columns and
/// context values (<see cref="PromptTemplate"/>).
/// </summary>
/// <remarks>
/// <para>Supported: variables (<c>{{name}}</c>, <c>{{{name}}}</c>, <c>{{&amp; name}}</c>), sections
/// (<c>{{#name}}…{{/name}}</c>), inverted sections (<c>{{^name}}…{{/name}}</c>), comments (<c>{{! … }}</c>), the implicit
/// iterator <c>{{.}}</c>, padding inside tags (<c>{{ name }}</c>), and the specification's standalone-line rule: a line
/// holding only whitespace and one section, inverted, closing or comment tag is removed whole, its newline included, so
/// block tags on lines of their own leave no blank lines.</para>
/// <para>Not supported, each a parse error: partials (<c>{{&gt; name}}</c>), since a prompt is one self-contained
/// artifact, and set delimiters (<c>{{=&lt;% %&gt;=}}</c>), since every template uses the standard braces.</para>
/// <para>No HTML escaping: a prompt is not HTML, and escaping would put entities like <c>&amp;quot;</c> in front of the
/// model. So <c>{{name}}</c> renders like <c>{{{name}}}</c>, a deliberate difference from mustache(5).</para>
/// </remarks>
internal sealed class MustacheTemplate
{
    private readonly MustacheNode[] _nodes;

    private MustacheTemplate(string source, MustacheNode[] nodes, int tagCount)
    {
        Source = source;
        _nodes = nodes;
        TagCount = tagCount;
    }

    public string Source { get; }

    /// <summary>The top-level nodes, in order.</summary>
    public IReadOnlyList<MustacheNode> Nodes => _nodes;

    /// <summary>The number of variables and sections (see <see cref="MustacheTag.Id"/>).</summary>
    public int TagCount { get; }

    /// <summary>
    /// The deepest nesting of sections a template may have. Binding and rendering recurse once per level, so without a
    /// bound a long template of nested sections overflowed the stack and ended the process at load (review 29); a prompt
    /// needs a few levels at most.
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>Parses <paramref name="source"/>; false with the first error when it is not a supported template.</summary>
    public static bool TryParse(string source, [NotNullWhen(true)] out MustacheTemplate? template, out MustacheError error)
    {
        ArgumentNullException.ThrowIfNull(source);
        template = null;
        if (!TryScan(source, out var tags, out error))
        {
            return false;
        }

        var root = new List<MustacheNode>();
        var open = new List<OpenSection>();
        var current = root;
        var cursor = 0;
        var ids = 0;
        foreach (var tag in tags)
        {
            var (from, to) = (tag.Start, tag.End);
            if (tag.Sigil is '#' or '^' or '/' or '!' && Standalone(source, tag.Start, tag.End, out var lineStart, out var lineEnd))
            {
                (from, to) = (lineStart, lineEnd);
            }

            if (from > cursor)
            {
                current.Add(new MustacheText(cursor, from - cursor));
            }

            cursor = to;
            switch (tag.Sigil)
            {
                case '!':
                    break;
                case '#' or '^':
                    if (open.Count == MaxDepth)
                    {
                        error = new MustacheError(tag.Start, "MUSTACHE_TOO_DEEP",
                            $"{Quote(source, tag)} opens a section {MaxDepth + 1} levels deep; a template may nest at most {MaxDepth}.",
                            "nest fewer sections: compute the condition as a derived feature and test that one value");
                        return false;
                    }

                    var children = new List<MustacheNode>();
                    open.Add(new OpenSection(tag, ids++, current, children));
                    current = children;
                    break;
                case '/':
                    if (open.Count == 0)
                    {
                        error = new MustacheError(tag.Start, "MUSTACHE_UNEXPECTED_CLOSE", $"{Quote(source, tag)} closes no section: none is open.",
                            $"remove it, or open the section before it with {{{{#{tag.Name}}}}} or {{{{^{tag.Name}}}}}");
                        return false;
                    }

                    var section = open[^1];
                    if (!string.Equals(section.Tag.Name, tag.Name, StringComparison.Ordinal))
                    {
                        error = new MustacheError(tag.Start, "MUSTACHE_SECTION_MISMATCH",
                            $"{Quote(source, tag)} closes '{tag.Name}', but the innermost open section is '{section.Tag.Name}' (opened at offset {section.Tag.Start}).",
                            $"close sections innermost first: {{{{/{section.Tag.Name}}}}} before {{{{/{tag.Name}}}}}");
                        return false;
                    }

                    open.RemoveAt(open.Count - 1);
                    var kind = section.Tag.Sigil == '#' ? MustacheTagKind.Section : MustacheTagKind.InvertedSection;
                    section.Parent.Add(new MustacheTag(kind, section.Tag.Name, section.Tag.Start, section.Tag.End - section.Tag.Start, section.Id, false,
                        section.Children.ToArray()));
                    current = section.Parent;
                    break;
                default:
                    current.Add(new MustacheTag(MustacheTagKind.Variable, tag.Name, tag.Start, tag.End - tag.Start, ids++, tag.Sigil is '&' or '{', null));
                    break;
            }
        }

        if (open.Count > 0)
        {
            var unclosed = open[^1].Tag;
            error = new MustacheError(unclosed.Start, "MUSTACHE_UNCLOSED_SECTION", $"Section {Quote(source, unclosed)} is never closed.",
                $"close it with {{{{/{unclosed.Name}}}}}");
            return false;
        }

        if (cursor < source.Length)
        {
            root.Add(new MustacheText(cursor, source.Length - cursor));
        }

        template = new MustacheTemplate(source, root.ToArray(), ids);
        error = default;
        return true;
    }

    /// <summary>Renders the template into <paramref name="output"/>, asking <paramref name="data"/> for every tag.</summary>
    public void Render<TData>(ref TData data, StringBuilder output)
        where TData : IMustacheData, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(output);
        Render(Source, _nodes, ref data, output);
    }

    private static void Render<TData>(string source, MustacheNode[] nodes, ref TData data, StringBuilder output)
        where TData : IMustacheData, allows ref struct
    {
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] is not MustacheTag tag)
            {
                output.Append(source, nodes[i].Offset, nodes[i].Length);
                continue;
            }

            switch (tag.Kind)
            {
                case MustacheTagKind.Variable:
                    data.Write(tag, output);
                    break;
                case MustacheTagKind.Section:
                    var count = data.Count(tag);
                    for (var item = 0; item < count; item++)
                    {
                        data.Enter(tag, item);
                        Render(source, tag.ChildNodes, ref data, output);
                        data.Leave(tag);
                    }

                    break;
                case MustacheTagKind.InvertedSection:
                    if (data.Count(tag) == 0)
                    {
                        Render(source, tag.ChildNodes, ref data, output);
                    }

                    break;
            }
        }
    }

    /// <summary>A tag as scanned: its sigil (<c>\0</c> for a plain variable, <c>{</c> for a triple mustache), name and extent.</summary>
    private readonly record struct RawTag(char Sigil, string Name, int Start, int End);

    private readonly record struct OpenSection(RawTag Tag, int Id, List<MustacheNode> Parent, List<MustacheNode> Children);

    /// <summary>Finds every tag in source order; false with the first malformed or unsupported one.</summary>
    private static bool TryScan(string source, out List<RawTag> tags, out MustacheError error)
    {
        tags = new List<RawTag>();
        error = default;
        var position = 0;
        while (position < source.Length)
        {
            var start = source.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var p = start + 2;
            var sigil = p < source.Length ? source[p] : '\0';
            var closer = "}}";
            switch (sigil)
            {
                case '{':
                    closer = "}}}";
                    p++;
                    break;
                case '&' or '#' or '^' or '/' or '!':
                    p++;
                    break;
                case '>':
                    error = new MustacheError(start, "MUSTACHE_PARTIAL", $"Partials ({Excerpt(source, start)}) are not supported.",
                        "write the text in the template itself: a prompt is one self-contained artifact, so it never includes another file");
                    return false;
                case '=':
                    error = new MustacheError(start, "MUSTACHE_DELIMITER", $"Set delimiters ({Excerpt(source, start)}) are not supported.",
                        "use the standard {{ }} delimiters; text that must contain '{{' can come from a value, e.g. a param");
                    return false;
                default:
                    sigil = '\0';
                    break;
            }

            var close = source.IndexOf(closer, p, StringComparison.Ordinal);
            if (close < 0)
            {
                error = new MustacheError(start, "MUSTACHE_UNCLOSED_TAG", $"The tag {Excerpt(source, start)} has no closing '{closer}'.",
                    $"close it with '{closer}'; text that must contain '{{{{' can come from a value, e.g. a param");
                return false;
            }

            var end = close + closer.Length;
            var name = sigil == '!' ? string.Empty : source[p..close].Trim();
            if (sigil != '!')
            {
                if (name.Length == 0)
                {
                    error = new MustacheError(start, "MUSTACHE_EMPTY_TAG", $"The tag {source[start..end]} names nothing.",
                        "write a name between the braces, e.g. {{ctx.request.surface}}, or remove the tag");
                    return false;
                }

                foreach (var c in name)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        error = new MustacheError(start, "MUSTACHE_TAG_NAME", $"The tag {source[start..end]} has whitespace inside its name '{name}'.",
                            "a tag holds one name, e.g. {{col.item.title}}; padding around it is allowed");
                        return false;
                    }
                }
            }

            tags.Add(new RawTag(sigil, name, start, end));
            position = end;
        }

        return true;
    }

    /// <summary>
    /// The specification's standalone rule: a tag alone on its line, with only spaces and tabs around it, up to a newline
    /// (<c>\n</c> or <c>\r\n</c>) or the end of the template. Then the whole line, newline included, is removed.
    /// </summary>
    private static bool Standalone(string source, int start, int end, out int lineStart, out int lineEnd)
    {
        lineStart = start;
        lineEnd = end;
        var i = start;
        while (i > 0 && source[i - 1] is ' ' or '\t')
        {
            i--;
        }

        if (i > 0 && source[i - 1] != '\n')
        {
            return false;
        }

        var j = end;
        while (j < source.Length && source[j] is ' ' or '\t')
        {
            j++;
        }

        if (j < source.Length)
        {
            if (source[j] == '\n')
            {
                j++;
            }
            else if (source[j] == '\r' && j + 1 < source.Length && source[j + 1] == '\n')
            {
                j += 2;
            }
            else
            {
                return false;
            }
        }

        (lineStart, lineEnd) = (i, j);
        return true;
    }

    private static string Quote(string source, in RawTag tag) => source[tag.Start..tag.End];

    /// <summary>The start of an unsupported or unclosed tag, for messages: up to its closing braces or 40 characters.</summary>
    private static string Excerpt(string source, int start)
    {
        var close = source.IndexOf("}}", start + 2, StringComparison.Ordinal);
        var end = close < 0 ? source.Length : close + 2;
        return end - start <= 40 ? source[start..end] : source.Substring(start, 40) + "…";
    }
}
