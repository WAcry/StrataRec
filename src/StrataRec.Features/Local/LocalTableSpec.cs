using System.Globalization;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Bindings;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;

namespace StrataRec.Features.Local;

/// <summary>
/// What a plan needs from one local feature table (design 6.6): the layout, the limits, the source that loads it, and
/// the <see cref="Identity"/> that decides whether a table loaded for an earlier plan can serve this one.
/// </summary>
public sealed class LocalTableSpec
{
    private LocalTableSpec(
        LocalTableSchema schema, LocalFeatureTableOptions options, FeatureSourceBinding binding, IFeatureUpdateSource bootstrap, string artifact,
        string pointer, string baseDirectory)
    {
        Schema = schema;
        Options = options;
        Bootstrap = bootstrap;
        Artifact = artifact;
        Pointer = pointer;
        BaseDirectory = baseDirectory;
        Identity = ComputeIdentity(schema, binding, baseDirectory);
    }

    /// <summary>
    /// Creates the spec and its bootstrap source with one base directory, pinned now (<see cref="BindingContext.PinBaseDirectory"/>, the rule every binding uses):
    /// the source resolves its relative paths against the same absolute directory the <see cref="Identity"/> names, so a
    /// later change of the process's current directory changes neither what the source reads nor which table is shared.
    /// </summary>
    /// <param name="binding">The source's binding as written (mode <c>local</c>, with a <c>bootstrap</c>); part of the identity.</param>
    /// <param name="bootstrapFactory">The factory of the binding's <c>bootstrap</c> source, which loads the table.</param>
    /// <param name="sourceName">The feature source's name, for the bootstrap source.</param>
    /// <param name="artifact">The bindings artifact, e.g. <c>bindings:local</c>, for load errors.</param>
    /// <param name="pointer">The binding's pointer, e.g. <c>/sources/item_store</c>, for load errors.</param>
    /// <param name="baseDirectory">Directory relative paths in the settings resolve against (relative: against the current
    /// directory; null: the current directory); pinned, it is part of the identity.</param>
    /// <exception cref="FeatureUpdateException">The bootstrap settings are wrong; the pointer is within <c>bootstrap</c>.</exception>
    public static LocalTableSpec Create(
        LocalTableSchema schema,
        LocalFeatureTableOptions options,
        FeatureSourceBinding binding,
        IFeatureUpdateSourceFactory bootstrapFactory,
        string sourceName,
        string artifact,
        string pointer,
        string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(bootstrapFactory);
        if (binding.Bootstrap is not { } bootstrap)
        {
            throw new ArgumentException("A local binding names its bootstrap source.", nameof(binding));
        }

        var directory = BindingContext.PinBaseDirectory(baseDirectory);
        var source = bootstrapFactory.Create(sourceName, bootstrap.Settings, directory);
        return new LocalTableSpec(schema, options, binding, source, artifact, pointer, directory);
    }

    /// <summary>
    /// The spec of a <c>cache</c> table (design 6.6): fed by the requests that miss it, through its writer, which the
    /// table host runs as the table's source (<see cref="Cache.CacheWriterSource"/>). The identity is built by the same rule
    /// as a local table's, so a change of the connector, of the cache settings or of the field set starts a new, empty table.
    /// </summary>
    /// <param name="options">With <see cref="LocalFeatureTableOptions.Cache"/> set.</param>
    public static LocalTableSpec CreateCache(
        LocalTableSchema schema, LocalFeatureTableOptions options, FeatureSourceBinding binding, string artifact, string pointer, string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(binding);
        if (schema.Mode != LocalTableMode.Cache || options.Cache is null)
        {
            throw new ArgumentException("A cache spec has a cache schema and cache options.", nameof(options));
        }

        return new LocalTableSpec(
            schema, options, binding, Cache.CacheWriterSource.Instance, artifact, pointer, BindingContext.PinBaseDirectory(baseDirectory));
    }

    public LocalTableSchema Schema { get; }

    public LocalFeatureTableOptions Options { get; }

    /// <summary>Loads the table (for a <c>cache</c> table, its writer). A table reused from an earlier plan keeps the source
    /// it was loaded by, which has the same identity and therefore the same settings.</summary>
    public IFeatureUpdateSource Bootstrap { get; }

    /// <summary>The bindings artifact, e.g. <c>bindings:local</c>.</summary>
    public string Artifact { get; }

    /// <summary>The binding's pointer in <see cref="Artifact"/>, e.g. <c>/sources/item_store</c>.</summary>
    public string Pointer { get; }

    /// <summary><c>source/entity</c>, how messages and logs name the table.</summary>
    public string Name => Schema.Source + "/" + Schema.Entity;

    /// <summary>The absolute directory the source's relative paths resolve against, pinned when the spec was created.</summary>
    public string BaseDirectory { get; }

    /// <summary>
    /// The one rule for reusing a table across plans: two specs with the same identity are served by one table, so a new
    /// plan takes over a loaded table instead of loading it again. The identity is the RFC 8785 canonical JSON of what
    /// decides the table's contents: the source and entity, the key type, the physical fields with their storage types
    /// (in field-name order, so renaming the features that read them, or changing their defaults, changes nothing), the
    /// source's binding as written, and the absolute <see cref="BaseDirectory"/>. Nothing else in the configuration
    /// is part of it: other sources, pipelines and experiments never cause a reload.
    /// </summary>
    /// <remarks>
    /// The binding is taken whole rather than setting by setting, so a binding setting added later is part of the
    /// identity without anyone remembering to add it: a table is never shared between bindings that differ in anything.
    /// The table options all come from the binding (<c>max_bytes</c>) or are the engine's constants.
    /// </remarks>
    public string Identity { get; }

    private static string ComputeIdentity(LocalTableSchema schema, FeatureSourceBinding binding, string baseDirectory)
    {
        var fields = new List<LocalTableField>(schema.Fields);
        fields.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var fieldArray = new JsonArray();
        foreach (var field in fields)
        {
            fieldArray.Add(new JsonArray(field.Name, field.Type.ToString()));
        }

        var identity = new JsonObject
        {
            ["source"] = schema.Source,
            ["entity"] = schema.Entity,
            ["key_type"] = schema.KeyType == EntityKeyType.String ? "string" : "int64",
            ["mode"] = schema.Mode.ToString(),
            ["fields"] = fieldArray,
            ["binding"] = ArtifactSerializer.ToNode(binding),
            ["base_directory"] = baseDirectory,
        };
        return JsonCanonicalizer.CanonicalizeToString(identity);
    }
}

/// <summary>Gives a plan being compiled the table of each local (source, entity) it reads (design 6.6).</summary>
/// <remarks>The engine's provider (<c>BindingLease</c> in Hosting) shares tables by <see cref="LocalTableSpec.Identity"/>
/// and loads them in the background; a plan is served only once its tables are loaded.</remarks>
public interface ILocalTableProvider
{
    /// <summary>The table for <paramref name="spec"/>: one already held with the same identity, or a new one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The spec's <c>max_bytes</c> cannot hold even an empty table.</exception>
    LocalFeatureTable Acquire(LocalTableSpec spec);
}

/// <summary>
/// Gives every plan new tables that are never loaded, for compiling without serving (validate, build, explain). A
/// request that reads such a table fails the features it needs with an error saying the table is not loaded, rather than
/// serving them as missing; a replay never reads it (it answers local reads from the recording).
/// </summary>
public sealed class UnloadedLocalTables : ILocalTableProvider
{
    public static UnloadedLocalTables Instance { get; } = new();

    public LocalFeatureTable Acquire(LocalTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new LocalFeatureTable(spec.Schema, spec.Options);
    }
}

/// <summary>Update-source factories available in this binary, keyed by type (<c>name@version</c>); the
/// counterpart of <see cref="Runtime.FeatureConnectorRegistry"/>.</summary>
public sealed class FeatureUpdateSourceRegistry
{
    private readonly Dictionary<string, IFeatureUpdateSourceFactory> _factories = new(StringComparer.Ordinal);

    public IEnumerable<string> Types
    {
        get
        {
            var types = new List<string>(_factories.Keys);
            types.Sort(StringComparer.Ordinal);
            return types;
        }
    }

    /// <summary>A registry with the built-in sources (<c>snapshot_files@1</c>).</summary>
    public static FeatureUpdateSourceRegistry CreateDefault() => new FeatureUpdateSourceRegistry().Add(new SnapshotFilesUpdateSourceFactory());

    public FeatureUpdateSourceRegistry Add(IFeatureUpdateSourceFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[factory.SourceType] = factory;
        return this;
    }

    public bool TryGet(string type, out IFeatureUpdateSourceFactory factory) => _factories.TryGetValue(type, out factory!);
}

/// <summary>
/// A local table could not be loaded for a plan: the first run of its bootstrap source failed before the table held a
/// complete load. The message names the table, the binding (artifact and pointer) and, through the inner
/// <see cref="FeatureUpdateException"/>, the file and line of the bad data.
/// </summary>
public sealed class LocalTableLoadException : Exception
{
    public LocalTableLoadException(LocalTableSpec spec, Exception innerException)
        : base(Format(spec, innerException), innerException)
    {
        Table = spec.Name;
        Artifact = spec.Artifact;
        Pointer = spec.Pointer;
        Hint = innerException switch
        {
            FeatureUpdateException update => update.Hint,
            LocalTableFullException => "raise max_bytes in the source's binding, or bind fewer or smaller features to the source",
            _ => null,
        };
    }

    /// <summary><c>source/entity</c>.</summary>
    public string Table { get; }

    /// <summary>The bindings artifact, e.g. <c>bindings:local</c>.</summary>
    public string Artifact { get; }

    /// <summary>The binding's pointer, e.g. <c>/sources/item_store</c>.</summary>
    public string Pointer { get; }

    /// <summary>How to fix it, when the source said.</summary>
    public string? Hint { get; }

    /// <summary>The message without the hint (the inner exception's own hint line is moved to <see cref="Hint"/>).</summary>
    private static string Format(LocalTableSpec spec, Exception inner)
    {
        var reason = inner switch
        {
            FeatureUpdateException update => update.Location + (update.Pointer.Length == 0 ? string.Empty : " " + update.Pointer) + ": " + update.Reason,
            LocalTableFullException full => string.Create(CultureInfo.InvariantCulture,
                $"the data needs more than max_bytes ({full.MaxBytes} B; the write would have taken the table to {full.RequiredBytes} B)"),
            _ => ExceptionText.Of(inner),
        };
        return $"Local table {spec.Name} (source '{spec.Schema.Source}' in {spec.Artifact}) failed to load from {spec.Bootstrap.SourceType}: {reason}";
    }
}

/// <summary>
/// Reads <c>max_bytes</c>: a non-negative integer and an IEC unit, e.g. <c>"512MiB"</c> (IEC 80000-13), the form of
/// <see cref="FeatureSourceBinding.ByteSizePattern"/> (the JSON Schema pattern of the setting).
/// </summary>
public static class ByteSize
{
    private static readonly (string Unit, int Shift)[] Units = { ("TiB", 40), ("GiB", 30), ("MiB", 20), ("KiB", 10), ("B", 0) };

    /// <summary>Parses <paramref name="text"/>; false when it is not a number and a unit, or does not fit an int64.</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var (unit, shift) in Units)
        {
            if (!text.EndsWith(unit, StringComparison.Ordinal))
            {
                continue;
            }

            var number = text.AsSpan(0, text.Length - unit.Length);
            if (number.IsEmpty || !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > long.MaxValue >> shift)
            {
                return false;
            }

            bytes = value << shift;
            return true;
        }

        return false;
    }
}
