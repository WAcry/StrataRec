using StrataRec.Expressions.Syntax;

namespace StrataRec.Expressions.Binding;

/// <summary>
/// What to say when an expression calls something CEL has and ColumnExpr spells differently or leaves out (design 7.1):
/// the one place that knows those names, so that an author, or an agent, who writes CEL gets the ColumnExpr way, or why
/// there is none, instead of an unknown function with a cascade of unknown names.
/// </summary>
/// <remarks>
/// A call is a CEL idiom by its name, or, for an extension, by its namespace (<c>cel.bind</c>, <c>math.greatest</c>): a
/// method call on a bare name that no let binding holds, since no other name of an expression is a single word. The
/// arguments of an idiom are not bound: those of a macro or of <c>cel.bind</c> declare a variable the rest refers to.
/// </remarks>
internal static class CelIdioms
{
    private const string Macro =
        "test membership with 'x in list' and count shared elements with overlap_count(a, b); other per-element logic belongs in the feature or in an operator";

    private const string Strings =
        "normalize strings where the feature is produced (case, trimming) and compare them with ==, 'in', startsWith() or endsWith()";

    private static readonly Dictionary<string, (string Message, string Hint)> ByName = new(StringComparer.Ordinal)
    {
        ["all"] = MacroEntry("all"),
        ["exists"] = MacroEntry("exists"),
        ["exists_one"] = MacroEntry("exists_one"),
        ["map"] = MacroEntry("map"),
        ["filter"] = MacroEntry("filter"),
        ["matches"] = ("Regular expressions ('matches') are not supported: a match per row costs too much on the scoring path",
            "use ==, 'in [...]', startsWith() or endsWith(), or compute a flag where the feature is produced"),
        ["orValue"] = ("CEL's optional values are written with ?? in ColumnExpr", "write 'x ?? fallback'"),
        ["hasValue"] = ("CEL's optional values are tested with has() in ColumnExpr", "write 'has(x)'"),
        ["string"] = ("There is no conversion to string",
            "compare a value with a literal of its own type (an int64 id with 1001, not '1001'), or produce the string in the feature"),
        ["bool"] = ("There is no conversion to bool", "compare explicitly, e.g. 'x != 0'"),
        ["uint"] = ("There are no unsigned integers: integers are int64", "use int(x)"),
        ["bytes"] = ("There is no bytes type", "compare strings, or produce the value in the feature"),
        ["dyn"] = ("Expressions are statically typed: there is no dyn()", "drop the call"),
        ["type"] = ("Expressions are statically typed: there is no type()", "test the value itself; its type is known when the configuration compiles"),
        ["duration"] = ("There are no time types", "use int64 milliseconds: ctx.request.time_ms, and features holding epoch milliseconds"),
        ["timestamp"] = ("There are no time types", "use int64 milliseconds: ctx.request.time_ms, and features holding epoch milliseconds"),
        ["lowerAscii"] = StringEntry("lowerAscii"),
        ["upperAscii"] = StringEntry("upperAscii"),
        ["trim"] = StringEntry("trim"),
        ["substring"] = StringEntry("substring"),
        ["charAt"] = StringEntry("charAt"),
        ["indexOf"] = StringEntry("indexOf"),
        ["lastIndexOf"] = StringEntry("lastIndexOf"),
        ["replace"] = StringEntry("replace"),
        ["split"] = StringEntry("split"),
        ["join"] = StringEntry("join"),
    };

    /// <summary>CEL extension namespaces: <c>ns.f(...)</c> is never a method call on a value.</summary>
    private static readonly HashSet<string> Namespaces = new(StringComparer.Ordinal) { "cel", "math", "sets", "strings", "optional", "lists", "base64" };

    /// <param name="isLet">Whether a bare name is a let binding in scope (then <c>name.f(...)</c> is a method call on it).</param>
    public static bool TryDescribe(CallAst call, Func<string, bool> isLet, out string message, out string hint, out int start)
    {
        start = call.NameStart;
        if (call.IsMethod && call.Args[0] is NameAst { Name: var ns } receiver && Namespaces.Contains(ns) && !isLet(ns))
        {
            start = receiver.Start;
            (message, hint) = Namespaced(ns, call.Name);
            return true;
        }

        if (!Builtins.IsFunction(call.Name) && ByName.TryGetValue(call.Name, out var entry))
        {
            (message, hint) = entry;
            return true;
        }

        (message, hint) = (string.Empty, string.Empty);
        return false;
    }

    private static (string Message, string Hint) Namespaced(string ns, string name) => (ns, name) switch
    {
        ("cel", "bind") => ("CEL's 'cel.bind' is written with let in ColumnExpr", "write 'let x = init; body' (each binding ends with ';')"),
        ("math", "greatest") => ("CEL's 'math.greatest' is max() in ColumnExpr", "write max(a, b, ...)"),
        ("math", "least") => ("CEL's 'math.least' is min() in ColumnExpr", "write min(a, b, ...)"),
        ("sets", "intersects") => ("CEL's 'sets.intersects' is written with overlap_count() in ColumnExpr", "write 'overlap_count(a, b) > 0'"),
        ("optional", _) => ("CEL's optional values are written with ?? and has() in ColumnExpr", "write 'x ?? fallback', or test has(x)"),
        _ when Builtins.IsFunction(name) => ($"ColumnExpr's functions have no namespace: '{ns}.{name}' is {name}()", $"write {name}(...)"),
        _ => ($"'{ns}.{name}' is a CEL extension ColumnExpr does not have", "see the built-in functions of design 7.6"),
    };

    private static (string, string) MacroEntry(string name) =>
        ($"CEL's macro '{name}' is not supported: an expression is evaluated a column at a time, without a loop over a list within a row", Macro);

    private static (string, string) StringEntry(string name) =>
        ($"'{name}' is not supported: strings support ==, !=, 'in', startsWith() and endsWith() only", Strings);
}
