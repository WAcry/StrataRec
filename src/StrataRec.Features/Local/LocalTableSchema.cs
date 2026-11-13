using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Features.Catalog;

namespace StrataRec.Features.Local;

/// <summary>How a local feature table is fed (design 6.6). The table works the same way in both modes; the mode says
/// what a record's version means and which maintenance applies.</summary>
public enum LocalTableMode : byte
{
    /// <summary>Snapshots and an update stream push every entity in. The version is the event version of the update;
    /// a key that is not in the table has no value (design 6.7, layer 3).</summary>
    Local = 1,

    /// <summary>Fetched values are written back on a miss. The version is the write time, so records can expire and be
    /// evicted by age (phase 2).</summary>
    Cache,
}

/// <summary>One physical field of a local table: the store's field name and its storage type (never nullable: whether a
/// record has a value is a validity bit, as in a column).</summary>
public readonly record struct LocalTableField(string Name, ColumnType Type);

/// <summary>
/// What one local feature table holds: the (source, entity) it serves, the key type and the physical fields bound to it
/// (design 6.6). The record layout follows from it and is fixed for the life of the table: a catalog that adds or retypes
/// a field of the source needs a new table.
/// </summary>
public sealed class LocalTableSchema
{
    private readonly Dictionary<string, int> _byName;

    public LocalTableSchema(string source, string entity, EntityKeyType keyType, LocalTableMode mode, IReadOnlyList<LocalTableField> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(entity);
        ArgumentNullException.ThrowIfNull(fields);
        if (keyType is not (EntityKeyType.Int64 or EntityKeyType.String))
        {
            throw new ArgumentOutOfRangeException(nameof(keyType), keyType, "Entity keys are int64 or string.");
        }

        if (mode is not (LocalTableMode.Local or LocalTableMode.Cache))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Local tables are in local or cache mode.");
        }

        Source = source;
        Entity = entity;
        KeyType = keyType;
        Mode = mode;
        var copy = new LocalTableField[fields.Count];
        _byName = new Dictionary<string, int>(fields.Count, StringComparer.Ordinal);
        for (var i = 0; i < copy.Length; i++)
        {
            var field = fields[i];
            ArgumentException.ThrowIfNullOrEmpty(field.Name, nameof(fields));
            if (field.Type.IsDefault || (field.Type.Kind == StorageKind.Vector && field.Type.Dimension <= 0))
            {
                throw new ArgumentException($"Field '{field.Name}' has no storage type (vectors need a dimension).", nameof(fields));
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

    /// <summary>Entity name, e.g. <c>item</c>.</summary>
    public string Entity { get; }

    public EntityKeyType KeyType { get; }

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
    /// <exception cref="ArgumentException">The entity is unknown or a cross entity, or two features disagree on a field's type.</exception>
    public static LocalTableSchema FromCatalog(FeatureCatalog catalog, string source, string entity, LocalTableMode mode)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.Entities.TryGetValue(entity, out var definition))
        {
            throw new ArgumentException($"Entity '{entity}' is not declared in catalog '{catalog.CatalogId}'.", nameof(entity));
        }

        if (definition.Kind == EntityKind.Cross)
        {
            // A cross entity is keyed by a context key and a candidate key; a local table record has one key.
            throw new ArgumentException(
                $"Entity '{entity}' is a cross entity (context key × candidate key); local tables hold one key per record. " +
                "Bind its source with mode 'fetch' (with \"lookup\": \"context\" for per-user data).", nameof(entity));
        }

        var keyType = definition.Kind == EntityKind.Context ? definition.ContextKeyType : definition.CandidateKeyType;
        var fields = new List<LocalTableField>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var feature in catalog.Features)
        {
            if (feature.IsDerived || feature.Entity.Name != entity || !string.Equals(feature.Source, source, StringComparison.Ordinal))
            {
                continue;
            }

            var name = feature.Field ?? throw new ArgumentException($"Feature '{feature.Name}' declares no physical field.", nameof(catalog));
            var type = feature.Type.AsNonNullable();
            if (byName.TryGetValue(name, out var existing))
            {
                if (fields[existing].Type != type)
                {
                    throw new ArgumentException(
                        $"Feature '{feature.Name}' reads field '{name}' of source '{source}' as {type}, but another feature reads it as " +
                        $"{fields[existing].Type}. One physical field has one storage type; declare both features with the same type.",
                        nameof(catalog));
                }

                continue;
            }

            byName[name] = fields.Count;
            fields.Add(new LocalTableField(name, type));
        }

        return new LocalTableSchema(source, entity, keyType ?? EntityKeyType.Int64, mode, fields);
    }
}
