using System.Globalization;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;

namespace StrataRec.Features.Catalog;

/// <summary>Where a configuration declares a type; each place accepts its own set of spellings.</summary>
public enum TypeSite : byte
{
    /// <summary>A column or a request input: every storage type, nullable with a trailing <c>?</c>.</summary>
    Column = 1,

    /// <summary>A feature or derived feature: every storage type, without <c>?</c> (a feature without a default is nullable).</summary>
    Feature,

    /// <summary>A parameter: int64, float64, bool, string or a map of one of them; never missing, so never <c>?</c>.</summary>
    Parameter,
}

/// <summary>
/// The type spellings each place of a configuration accepts (<see cref="TypeSite"/>), and the error for one it does
/// not: a "did you mean" when a valid spelling is close (or the place's spelling of a name other languages use, such as
/// <c>double</c>), and the spellings valid there.
/// </summary>
public static class ConfigTypes
{
    private static readonly string[] ColumnSpellings =
    {
        "bool", "int32", "int64", "float32", "float64", "string", "list<E>",
    };

    /// <summary>A list's element types (<see cref="ColumnType.IsListElement"/>), as written.</summary>
    private static readonly string[] ElementSpellings = { "bool", "int32", "int64", "float32", "float64", "string" };

    private static readonly string ListElements =
        "list<E> or list<E,N> (exactly N elements, e.g. an embedding list<float32,64>) with E one of " + string.Join(", ", ElementSpellings);

    private static readonly string[] ParameterLeaves = { "int64", "float64", "bool", "string" };

    private static string ColumnList => string.Join(", ", ColumnSpellings[..^1]) + ", " + ListElements;

    /// <summary>The spellings valid at <paramref name="site"/>, for a hint.</summary>
    public static string Spellings(TypeSite site) => site switch
    {
        TypeSite.Column => ColumnList + ", each with a trailing '?' to be nullable",
        TypeSite.Feature => ColumnList + " (a feature without 'default' is nullable; no '?')",
        _ => string.Join(", ", ParameterLeaves) + ", or map<string,T> of one of them",
    };

    /// <summary>
    /// On failure, <paramref name="message"/> says what is wrong with the spelling and <paramref name="hint"/> suggests
    /// one (or lists the valid ones).
    /// </summary>
    /// <param name="type">The type; for a map parameter, the type of its values.</param>
    /// <param name="isMap">True for a parameter written <c>map&lt;string,T&gt;</c>.</param>
    public static bool TryParse(string? text, TypeSite site, out ColumnType type, out bool isMap, out string message, out string hint)
    {
        type = default;
        isMap = false;
        message = hint = string.Empty;
        var written = text?.Trim() ?? string.Empty;
        if (written.Length == 0)
        {
            message = "The type is empty.";
            hint = "write one of: " + Spellings(site);
            return false;
        }

        var leaf = written;
        if (site == TypeSite.Parameter && written.StartsWith("map<", StringComparison.Ordinal) && written.EndsWith('>'))
        {
            if (!written.StartsWith("map<string,", StringComparison.Ordinal))
            {
                message = $"Map parameters have string keys, got '{written}'.";
                hint = $"write map<string,T> with T one of {string.Join(", ", ParameterLeaves)}";
                return false;
            }

            isMap = true;
            leaf = written["map<string,".Length..^1].Trim();
        }

        if (ColumnType.TryParse(leaf, out type, out _))
        {
            if (site == TypeSite.Feature && type.Nullable)
            {
                message = $"Feature types are written without '?'; nullability comes from 'default' (got '{written}').";
                hint = "remove the '?' and omit 'default' to make the feature nullable";
                return false;
            }

            if (site == TypeSite.Parameter && !IsParameterLeaf(type))
            {
                message = type.Nullable
                    ? $"Parameter types are written without '?': a parameter always has a value (got '{written}')."
                    : $"'{leaf}' is not a parameter type.";
                var meant = type.AsNonNullable().Promoted();
                hint = IsParameterLeaf(meant)
                    ? $"write '{Wrap(meant.ToString(), isMap)}'; parameter types: {Spellings(site)}"
                    : "parameter types: " + Spellings(site);
                return false;
            }

            return true;
        }

        message = site == TypeSite.Parameter ? $"Unknown parameter type '{written}'." : $"Unknown type '{written}'.";
        if (ColumnType.TextAsString(leaf) is not null)
        {
            message = $"{message[..^1]}: {ColumnType.NoTextType}.";
            if (site == TypeSite.Parameter && leaf.StartsWith("list<", StringComparison.Ordinal))
            {
                hint = "a parameter is never a list; parameter types: " + Spellings(site);
                return false;
            }
        }
        else if (ColumnType.VectorAsList(leaf) is not null)
        {
            message = $"{message[..^1]}: a vector is a list of fixed length, list<E,N>.";
            if (site == TypeSite.Parameter)
            {
                hint = "a parameter is never a list; parameter types: " + Spellings(site);
                return false;
            }
        }
        else if (site != TypeSite.Parameter && leaf.StartsWith("list<", StringComparison.Ordinal) && !ColumnType.TryParse(leaf, out _, out var why))
        {
            message = site == TypeSite.Feature && leaf.EndsWith("?>", StringComparison.Ordinal)
                ? $"Unknown type '{written}': a list's elements are never missing, and a feature without 'default' is the one that may be missing."
                : why;
        }

        var suggestion = Suggest(leaf, site);
        hint = suggestion is null
            ? "valid types: " + Spellings(site)
            : $"did you mean '{Wrap(suggestion, isMap)}'? Valid types: {Spellings(site)}";
        return false;
    }

    internal static string? Suggest(string written, TypeSite site)
    {
        var nullable = written.EndsWith('?') && site == TypeSite.Column;
        var core = (written.EndsWith('?') ? written[..^1] : written).Trim().ToLowerInvariant();
        var candidates = site == TypeSite.Parameter ? ParameterLeaves : ColumnSpellings;
        var suggestion = Alias(core, site);
        if (suggestion is null && site != TypeSite.Parameter)
        {
            suggestion = ColumnType.VectorAsList(core);
        }

        if (suggestion is null && site != TypeSite.Parameter && ListInner(core) is { } listInner)
        {
            var inner = listInner;
            var length = string.Empty;
            var comma = inner.LastIndexOf(',');
            if (comma >= 0)
            {
                var n = inner[(comma + 1)..].Trim();
                length = int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0 ? "," + n : string.Empty;
                inner = inner[..comma].Trim();
            }

            var element = inner.EndsWith('?') ? inner[..^1].Trim() : inner;
            var meant = Alias(element, site) ?? (Array.IndexOf(ElementSpellings, element) >= 0 ? element : NameSuggestions.Closest(element, ElementSpellings));
            // A nullable element means a list that may be missing: list<E>? where '?' is written, a plain list elsewhere.
            suggestion = meant is null ? null : $"list<{meant}{length}>" + (inner.EndsWith('?') && !nullable && site == TypeSite.Column ? "?" : string.Empty);
        }

        if (suggestion is null && Array.IndexOf(candidates, core) >= 0)
        {
            suggestion = core;
        }

        suggestion ??= NameSuggestions.Closest(core, candidates);
        return suggestion is null ? null : nullable ? suggestion + "?" : suggestion;
    }

    private static string? Alias(string name, TypeSite site) => name switch
    {
        "int" or "integer" or "long" => "int64",
        "short" => site == TypeSite.Parameter ? "int64" : "int32",
        "float" => site == TypeSite.Parameter ? "float64" : "float32",
        "double" or "decimal" or "number" => "float64",
        "boolean" => "bool",
        "str" or "char" or "text" => "string",
        _ => null,
    };

    private static string Wrap(string leaf, bool isMap) => isMap ? $"map<string,{leaf}>" : leaf;

    private static string? ListInner(string core)
    {
        foreach (var prefix in new[] { "list<", "array<", "sequence<", "seq<" })
        {
            if (core.StartsWith(prefix, StringComparison.Ordinal) && core.EndsWith('>'))
            {
                return core[prefix.Length..^1].Trim();
            }
        }

        return core.EndsWith("[]", StringComparison.Ordinal) ? core[..^2].Trim() : null;
    }

    private static bool IsParameterLeaf(ColumnType type) =>
        !type.Nullable && type.Kind is StorageKind.Int64 or StorageKind.Float64 or StorageKind.Bool or StorageKind.String;
}
