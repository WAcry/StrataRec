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

    public static DataLookupTable Create(DataTableArtifact artifact)
    {
        var keyType = ColumnType.Parse(artifact.KeyType).Promoted();
        var valueType = ColumnType.Parse(artifact.ValueType).Promoted();
        if (keyType.Kind is not (StorageKind.Int64 or StorageKind.String))
        {
            throw new FormatException($"Table '{artifact.TableId}': key_type must be int64 or string.");
        }

        if (valueType.Nullable || valueType.Kind is not (StorageKind.Int64 or StorageKind.Float64 or StorageKind.Bool or StorageKind.String))
        {
            throw new FormatException($"Table '{artifact.TableId}': value_type must be int64, float64, bool or string.");
        }

        var entries = new Dictionary<ScalarValue, ScalarValue>(artifact.Entries.Count);
        foreach (var (key, value) in artifact.Entries)
        {
            ScalarValue k;
            if (keyType.Kind == StorageKind.Int64)
            {
                if (!long.TryParse(key, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l))
                {
                    throw new FormatException($"Table '{artifact.TableId}': key '{key}' is not an int64.");
                }

                k = ScalarValue.FromInt64(l);
            }
            else
            {
                k = ScalarValue.FromString(key);
            }

            if (!ConfigValues.TryToScalar(value, valueType, out var v, out var error))
            {
                throw new FormatException($"Table '{artifact.TableId}': entry '{key}': {error}.");
            }

            entries[k] = v;
        }

        return new DataLookupTable(artifact.TableId, keyType, valueType, entries);
    }
}
