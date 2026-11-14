using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace StrataRec.Artifacts.Json;

/// <summary>
/// What a configuration member means, for the hint of an error about it: its <see cref="DescriptionAttribute"/>, else the
/// summary of its XML documentation when the assembly's documentation file is deployed next to it (the build writes one
/// for every project). The types that bind configuration are documented for their readers already, so errors reuse that
/// text instead of a second copy that would drift.
/// </summary>
/// <remarks>
/// Error path only: a documentation file is read once, on the first error that needs it. A summary that refers to a type
/// or method (<c>&lt;see cref="T:…"/&gt;</c>) is not used, since it would show a .NET name to someone writing JSON; a
/// property reference becomes the member's JSON name.
/// </remarks>
internal static class MemberDocs
{
    private static readonly ConcurrentDictionary<Assembly, Dictionary<string, string>> Summaries = new();

    /// <summary>The member's description as one line, or null when it has none that can be shown.</summary>
    /// <param name="naming">The naming policy of the serializer that binds the member, for property references.</param>
    public static string? Describe(MemberInfo member, JsonNamingPolicy? naming)
    {
        if (member.GetCustomAttribute<DescriptionAttribute>(inherit: true) is { Description: { Length: > 0 } description })
        {
            return description;
        }

        if (member.DeclaringType is not { FullName: { } typeName } type)
        {
            return null;
        }

        var prefix = member.MemberType switch
        {
            MemberTypes.Property => "P:",
            MemberTypes.Field => "F:",
            _ => null,
        };
        if (prefix is null)
        {
            return null;
        }

        var summaries = Summaries.GetOrAdd(type.Assembly, Load);
        return summaries.TryGetValue(prefix + typeName.Replace('+', '.') + "." + member.Name, out var xml) ? Flatten(xml, naming) : null;
    }

    private static Dictionary<string, string> Load(Assembly assembly)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        string path;
        try
        {
            path = assembly.Location; // empty in a single-file app: no documentation then
        }
        catch (NotSupportedException)
        {
            return summaries;
        }

        if (path.Length == 0)
        {
            return summaries;
        }

        path = Path.ChangeExtension(path, ".xml");
        if (!File.Exists(path))
        {
            return summaries;
        }

        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true });
            while (reader.ReadToFollowing("member"))
            {
                var name = reader.GetAttribute("name");
                if (name is null || !(name.StartsWith("P:", StringComparison.Ordinal) || name.StartsWith("F:", StringComparison.Ordinal)))
                {
                    continue;
                }

                using var member = reader.ReadSubtree();
                if (member.ReadToDescendant("summary"))
                {
                    summaries[name] = member.ReadInnerXml();
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable documentation file only costs the hint.
        }

        return summaries;
    }

    /// <summary>The text of a summary on one line, or null when it refers to something a JSON author cannot name.</summary>
    internal static string? Flatten(string xml, JsonNamingPolicy? naming)
    {
        var text = new StringBuilder();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.Element when reader.IsEmptyElement:
                        if (!AppendReference(reader, text, naming))
                        {
                            return null;
                        }

                        break;
                    case XmlNodeType.Element when reader.LocalName is "see" or "seealso" && reader.GetAttribute("cref") is { } cref && !IsMember(cref):
                        return null; // <see cref="T:X">text</see> still names a type
                }
            }
        }
        catch (XmlException)
        {
            return null;
        }

        var line = new StringBuilder(text.Length);
        foreach (var c in text.ToString())
        {
            if (char.IsWhiteSpace(c))
            {
                if (line.Length > 0 && line[^1] != ' ')
                {
                    line.Append(' ');
                }
            }
            else
            {
                line.Append(c);
            }
        }

        var result = line.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static bool AppendReference(XmlReader reader, StringBuilder text, JsonNamingPolicy? naming)
    {
        switch (reader.LocalName)
        {
            case "see" or "seealso":
                if (reader.GetAttribute("langword") is { } word)
                {
                    text.Append(word);
                    return true;
                }

                if (reader.GetAttribute("cref") is { } cref)
                {
                    if (!IsMember(cref))
                    {
                        return false;
                    }

                    var name = cref[(cref.LastIndexOf('.') + 1)..];
                    text.Append(naming?.ConvertName(name) ?? name);
                    return true;
                }

                if (reader.GetAttribute("href") is { } href)
                {
                    text.Append(href);
                }

                return true;
            case "paramref" or "typeparamref":
                text.Append(reader.GetAttribute("name"));
                return true;
            default:
                return true; // <para/>, <br/>: nothing to show
        }
    }

    /// <summary>A property or field reference, which reads as the member's JSON name.</summary>
    private static bool IsMember(string cref) => cref.StartsWith("P:", StringComparison.Ordinal) || cref.StartsWith("F:", StringComparison.Ordinal);
}
