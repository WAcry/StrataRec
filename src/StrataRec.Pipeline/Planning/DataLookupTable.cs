using System.Globalization;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Features.Catalog;

namespace StrataRec.Pipeline.Planning;

/// <summary>An immutable lookup table built from a <see cref="DataTableArtifact"/> (design 7.6).</summary>
public sealed class DataLookupTable : ILookupTable
{
    private readonly Dictionary<ScalarValue, ScalarValue> _entries;

    private DataLookupTable(string name, ColumnType keyType, ColumnType valueType, Dictionary<ScalarValue, ScalarValue> entries)
    {
        Name = name;
        KeyType = keyType;
        ValueType = valueType;
        _entries = entries;
    }

    public string Name { get; }

    public ColumnType KeyType { get; }

    public ColumnType ValueType { get; }

    public int Count => _entries.Count;

    public bool TryGet(in ScalarValue key, out ScalarValue value) => _entries.TryGetValue(key, out value);

    /// <summary>Builds the table, or reports what is wrong at its pointer (code <c>TABLE_INVALID</c>) and returns null.</summary>
    public static DataLookupTable? Create(DataTableArtifact artifact, DiagnosticBag diagnostics)
    {
        var label = "table:" + artifact.TableId;
        if (!TryType(artifact.KeyType, KeyTypes, "/key_type", "key type", label, diagnostics, out var keyType)
            | !TryType(artifact.ValueType, ValueTypes, "/value_type", "value type", label, diagnostics, out var valueType))
        {
            return null;
        }

        var ok = true;
        var entries = new Dictionary<ScalarValue, ScalarValue>(artifact.Entries.Count);
        foreach (var (key, value) in artifact.Entries)
        {
            var pointer = "/entries/" + JsonPointer.Escape(key);
            ScalarValue k;
            if (keyType.Kind == StorageKind.Int64)
            {
                if (!long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    diagnostics.Error("TABLE_INVALID", label, pointer, $"Key '{key}' is not an integer, and the table's key_type is int64.",
                        "write each key as the digits of an int64, e.g. \"42\", or declare \"key_type\": \"string\"");
                    ok = false;
                    continue;
                }

                k = ScalarValue.FromInt64(l);
            }
            else
            {
                k = ScalarValue.FromString(key);
            }

            if (!ConfigValues.TryToScalar(value, valueType, out var v, out var error))
            {
                diagnostics.Error("TABLE_INVALID", label, pointer, $"The value of '{key}' {error}.",
                    $"every value is {ConfigValues.Describe(valueType)}, as value_type declares");
                ok = false;
                continue;
            }

            entries[k] = v;
        }

        return ok ? new DataLookupTable(artifact.TableId, keyType, valueType, entries) : null;
    }

    private static readonly string[] KeyTypes = ["int64", "string"];
    private static readonly string[] ValueTypes = ["int64", "float64", "bool", "string"];

    private static bool TryType(string text, string[] allowed, string pointer, string what, string label, DiagnosticBag diagnostics, out ColumnType type)
    {
        foreach (var name in allowed)
        {
            if (string.Equals(text, name, StringComparison.Ordinal))
            {
                type = ColumnType.Parse(name);
                return true;
            }
        }

        type = default;
        var list = string.Join(", ", allowed);
        diagnostics.Error("TABLE_INVALID", label, pointer, $"'{text}' is not a {what} of a table.",
            NameSuggestions.DidYouMean(text, allowed) is { } suggestion ? $"{suggestion} A table's {what} is one of: {list}" : $"a table's {what} is one of: {list}");
        return false;
    }
}
