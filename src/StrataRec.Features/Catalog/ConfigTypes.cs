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
/// The type spellings each place of a configuration accepts (<see cref="TypeSite"/>), and the error for one it does not:
/// a "did you mean" when a valid spelling is close (or the place's spelling of a name other languages use, such as
/// <c>double</c>), and the spellings valid there. The one place that says which types columns, features and parameters
/// take, so their errors agree with what they accept.
/// </summary>
public static class ConfigTypes
{
    private static readonly string[] ColumnSpellings =
    {
        "bool", "int32", "int64", "float32", "float64", "string", "text", "vec<float32,N>", "list<int32>", "list<int64>", "list<string>",
    };

    private static readonly string[] ParameterLeaves = { "int64", "float64", "bool", "string" };

    /// <summary>The spellings valid at <paramref name="site"/>, for a hint.</summary>
    public static string Spellings(TypeSite site) => site switch
    {
        TypeSite.Column => string.Join(", ", ColumnSpellings) + ", each with a trailing '?' to be nullable",
        TypeSite.Feature => string.Join(", ", ColumnSpellings) + " (a feature without 'default' is nullable; no '?')",
        _ => string.Join(", ", ParameterLeaves) + ", or map<string,T> of one of them",
    };

    /// <summary>
    /// Parses a type written at <paramref name="site"/>. On failure, <paramref name="message"/> says what is wrong with the
    /// spelling and <paramref name="hint"/> suggests one (or lists the valid ones).
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
                // Both mistakes at once (int32?) get one fix: the parameter type the spelling meant, which params accept.
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
        var suggestion = Suggest(leaf, site);
        hint = suggestion is null
            ? "valid types: " + Spellings(site)
            : $"did you mean '{Wrap(suggestion, isMap)}'? Valid types: {Spellings(site)}";
        return false;
    }

    /// <summary>A valid spelling close to <paramref name="written"/> (a typo, another case, or another language's name), or null.</summary>
    internal static string? Suggest(string written, TypeSite site)
    {
        var nullable = written.EndsWith('?') && site == TypeSite.Column;
        var core = (written.EndsWith('?') ? written[..^1] : written).Trim().ToLowerInvariant();
        var candidates = site == TypeSite.Parameter ? ParameterLeaves : ColumnSpellings;
        var suggestion = Alias(core, site);
        if (suggestion is null && site != TypeSite.Parameter && core.StartsWith("vec", StringComparison.Ordinal))
        {
            // Keep the dimension written, e.g. vec<float64,4> → vec<float32,4>.
            var comma = core.LastIndexOf(',');
            var close = core.LastIndexOf('>');
            suggestion = comma > 0 && close > comma && int.TryParse(core.AsSpan(comma + 1, close - comma - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var dim) && dim > 0
                ? $"vec<float32,{dim}>"
                : "vec<float32,N>";
        }

        if (suggestion is null && Array.IndexOf(candidates, core) >= 0)
        {
            suggestion = core; // only the case differed
        }

        suggestion ??= NameSuggestions.Closest(core, candidates);
        return suggestion is null ? null : nullable ? suggestion + "?" : suggestion;
    }

    /// <summary>What another language calls a type, in this place's spelling.</summary>
    private static string? Alias(string name, TypeSite site) => name switch
    {
        "int" or "integer" or "long" => "int64",
        "short" => site == TypeSite.Parameter ? "int64" : "int32",
        "float" => site == TypeSite.Parameter ? "float64" : "float32",
        "double" or "decimal" or "number" => "float64",
        "boolean" => "bool",
        "str" or "char" => "string",
        _ => null,
    };

    private static string Wrap(string leaf, bool isMap) => isMap ? $"map<string,{leaf}>" : leaf;

    /// <summary>A type a parameter (or a map parameter's values) takes: int64, float64, bool or string, never nullable.</summary>
    private static bool IsParameterLeaf(ColumnType type) =>
        !type.Nullable && type.Kind is StorageKind.Int64 or StorageKind.Float64 or StorageKind.Bool or StorageKind.String;
}
