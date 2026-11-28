using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Catalog;

namespace StrataRec.Features.Local;

/// <summary>How a local feature table is fed. The table works the same way in both modes; the mode says
/// what a record's version means and which maintenance applies.</summary>
public enum LocalTableMode : byte
{
    /// <summary>Snapshots and an update stream push every entity in. The version is the event version of the update; a
    /// key that is not in the table has no value.</summary>
    Local = 1,

    /// <summary>Fetched values are written back on a miss. The version is the write time, so records can expire and be
    /// evicted by age.</summary>
    Cache,
}

/// <summary>One physical field of a local table: the store's field name and its storage type (never
/// nullable).</summary>
public readonly record struct LocalTableField(string Name, ColumnType Type);

/// <summary>
/// What one local feature table holds: the (source, entity) it serves, the key layout and the physical fields bound to it
///. The record layout follows from it and is fixed for the life of the table: a catalog that adds or retypes
/// a field of the source, or keys the entity by parts of other types, needs a new table.
/// </summary>
public sealed class LocalTableSchema
{
    private readonly Dictionary<string, int> _byName;

    /// <summary>A table of one-part keys of <paramref name="keyType"/>.</summary>
    public LocalTableSchema(string source, string entity, EntityKeyType keyType, LocalTableMode mode, IReadOnlyList<LocalTableField> fields)
        : this(source, entity, EntityKeyLayout.Single(keyType), mode, fields)
    {
    }

    public LocalTableSchema(string source, string entity, EntityKeyLayout key, LocalTableMode mode, IReadOnlyList<LocalTableField> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(entity);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fields);
        if (mode is not (LocalTableMode.Local or LocalTableMode.Cache))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Local tables are in local or cache mode.");
        }

        Source = source;
        Entity = entity;
        Key = key;
        Mode = mode;
        var copy = new LocalTableField[fields.Count];
        _byName = new Dictionary<string, int>(fields.Count, StringComparer.Ordinal);
        for (var i = 0; i < copy.Length; i++)
        {
            var field = fields[i];
            ArgumentException.ThrowIfNullOrEmpty(field.Name, nameof(fields));
            if (field.Type.IsDefault)
            {
                throw new ArgumentException($"Field '{field.Name}' has no storage type.", nameof(fields));
            }

            if (!_byName.TryAdd(field.Name, i))
            {
                throw new ArgumentException($"Field '{field.Name}' is listed twice.", nameof(fields));
            }

            copy[i] = new LocalTableField(field.Name, field.Type.AsNonNullable());
        }

        Fields = copy;
    }

    /// <summary>Logical source name, e.g. <c>item_store</c>.</summary>
    public string Source { get; }

    public string Entity { get; }

    /// <summary>The entity's key layout. What the table stores is its part types in order (<see cref="EntityKeyLayout.TypeText"/>).</summary>
    public EntityKeyLayout Key { get; }

    /// <summary>The type of a one-part key, or null for a key of several parts.</summary>
    public EntityKeyType? KeyType => Key.PartCount == 1 ? Key[0].Type : null;

    public LocalTableMode Mode { get; }

    /// <summary>Fields in field-index order: index <c>i</c> is what every reader and writer call names as field <c>i</c>.</summary>
    public IReadOnlyList<LocalTableField> Fields { get; }

    public int FieldCount => Fields.Count;

    public bool TryGetFieldIndex(string name, out int index) => _byName.TryGetValue(name, out index);

    /// <summary>The index of a physical field; throws with the known names when there is none.</summary>
    public int FieldIndex(string name) => _byName.TryGetValue(name, out var index)
        ? index
        : throw new KeyNotFoundException($"Local table {Source}/{Entity} has no field '{name}'." +
            (NameSuggestions.DidYouMean(name, _byName.Keys) is { } hint ? " " + hint : string.Empty));

    /// <summary>
    /// The schema of the table serving <paramref name="entity"/>'s features bound to <paramref name="source"/>: every
    /// physical feature of that entity whose <c>source</c> is <paramref name="source"/>, in catalog (name) order. Two
    /// features may read the same physical field only with the same type.
    /// </summary>
    /// <exception cref="ArgumentException">The entity is unknown, or two features disagree on a field's type.</exception>
    public static LocalTableSchema FromCatalog(FeatureCatalog catalog, string source, string entity, LocalTableMode mode)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.Entities.TryGetValue(entity, out var definition))
        {
            throw new ArgumentException($"Entity '{entity}' is not declared in catalog '{catalog.CatalogId}'.", nameof(entity));
        }

        var features = new List<FeatureDefinition>();
        foreach (var feature in catalog.Features)
        {
            if (!feature.IsDerived && feature.Entity.Name == entity && string.Equals(feature.Source, source, StringComparison.Ordinal))
            {
                features.Add(feature);
            }
        }

        if (!TryFromFeatures(source, definition, mode, features, out var schema, out var conflict))
        {
            throw new ArgumentException(conflict, nameof(catalog));
        }

        return schema;
    }

    /// <summary>
    /// The schema of the table holding <paramref name="features"/> (physical features of <paramref name="entity"/>
    /// bound to <paramref name="source"/>): one field per distinct physical field, in field-name order: one physical
    /// field has one storage type, so two features reading it with different types are a configuration error, returned
    /// in <paramref name="conflict"/> (and false) with the second feature.
    /// </summary>
    public static bool TryFromFeatures(
        string source,
        EntityDefinition entity,
        LocalTableMode mode,
        IReadOnlyList<FeatureDefinition> features,
        out LocalTableSchema schema,
        out string? conflict)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(features);
        schema = null!;
        conflict = null;
        var types = new SortedDictionary<string, ColumnType>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            var name = feature.Field ?? throw new ArgumentException($"Feature '{feature.Name}' declares no physical field.", nameof(features));
            var type = feature.Type.AsNonNullable();
            if (types.TryGetValue(name, out var existing))
            {
                if (existing != type)
                {
                    conflict = $"Feature '{feature.Name}' reads field '{name}' of source '{source}' as {type}, but another feature reads it as " +
                        $"{existing}. One physical field has one storage type in a local table; declare both features with the same type.";
                    return false;
                }

                continue;
            }

            types[name] = type;
        }

        var fields = new List<LocalTableField>(types.Count);
        foreach (var (name, type) in types)
        {
            fields.Add(new LocalTableField(name, type));
        }

        schema = new LocalTableSchema(source, entity.Name, entity.KeyLayout, mode, fields);
        return true;
    }

    internal string KeyHint()
    {
        if (Key.PartCount == 1)
        {
            return Key[0].Type == EntityKeyType.String ? "a JSON string" : "a JSON number (int64)";
        }

        var text = new System.Text.StringBuilder("an array of its ").Append(Key.PartCount).Append(" parts in order, [");
        for (var p = 0; p < Key.PartCount; p++)
        {
            text.Append(p == 0 ? string.Empty : ", ").Append(Key[p].Type == EntityKeyType.String ? "<string>" : "<int64>");
        }

        return text.Append(']').ToString();
    }
}
