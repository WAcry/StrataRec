using System.Text.Json;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;

namespace StrataRec.Features.Runtime;

/// <summary>How a feature source's values reach a request (design 6.6); chosen by the binding's <c>mode</c>.</summary>
public enum SupplyMode : byte
{
    /// <summary>Fetched from the source's connector by every request that needs them (the default).</summary>
    Fetch = 1,

    /// <summary>Read from an in-process table that an update source loads in the background: no I/O on the request path.</summary>
    Local,
}

/// <summary>The local table a <see cref="FetchGroup"/> reads (design 6.6), and where each of the group's fields is in it.</summary>
public sealed class LocalGroupTable
{
    internal LocalGroupTable(LocalTableSpec spec, LocalFeatureTable table, int[] tableFields)
    {
        Spec = spec;
        Table = table;
        TableFields = tableFields;
    }

    public LocalTableSpec Spec { get; }

    /// <summary>The table, shared with every plan whose spec has the same identity.</summary>
    public LocalFeatureTable Table { get; }

    /// <summary>Table field of each group member: <c>TableFields[i]</c> holds <see cref="FetchGroup.Fields"/>[i].</summary>
    public int[] TableFields { get; }
}

/// <summary>
/// Turns the bindings of a plan's feature sources into how each (source, entity) group is supplied (design 6.4, 6.6).
/// The one place that knows the supply modes: which binding settings belong to which mode, what each mode requires, and
/// what it needs from the binary (a connector; an update source and a local table). Adding a mode means adding it here
/// and to the fetcher's read path; plan building, the engine's table host and replay take groups as they come.
/// </summary>
/// <remarks>Only bindings the plan's features use are resolved, so a pipeline is never rejected for another
/// pipeline's source. Every problem is a diagnostic with the binding's pointer and a hint.</remarks>
internal sealed class FeatureSourceResolver
{
    /// <summary>
    /// Every setting of <see cref="FeatureSourceBinding"/> (by its JSON name) with the mode it belongs to; the members
    /// the binding does not declare are connector settings, mode fetch. A setting of the other mode is an error. A test
    /// checks that this lists every declared member, so a setting added to the binding cannot be silently accepted in a
    /// mode that ignores it.
    /// </summary>
    internal static readonly (string Name, SupplyMode Mode, Func<FeatureSourceBinding, bool> IsSet)[] ModeSettings =
    {
        ("connector", SupplyMode.Fetch, static b => b.Connector is not null),
        ("timeout_ms", SupplyMode.Fetch, static b => b.TimeoutMs is not null),
        ("max_batch", SupplyMode.Fetch, static b => b.MaxBatch is not null),
        ("lookup", SupplyMode.Fetch, static b => b.Lookup is not null),
        ("max_bytes", SupplyMode.Local, static b => b.MaxBytes is not null),
        ("bootstrap", SupplyMode.Local, static b => b.Bootstrap is not null),
    };

    private const int DefaultTimeoutMs = 50;
    private const int DefaultMaxBatch = 1000;

    /// <summary>A local group's chunk size when a replay answers its reads through the fetch path: one chunk, as recorded.</summary>
    private const int LocalMaxBatch = 1 << 30;

    private readonly SourceBindingArtifact _bindings;
    private readonly FeatureConnectorRegistry _connectors;
    private readonly FeatureUpdateSourceRegistry _updateSources;
    private readonly ILocalTableProvider _localTables;
    private readonly string? _baseDirectory;
    private readonly DiagnosticBag _diagnostics;
    private readonly string _artifact;
    private readonly Dictionary<string, SourceSupply?> _resolved = new(StringComparer.Ordinal);

    public FeatureSourceResolver(
        SourceBindingArtifact bindings,
        FeatureConnectorRegistry connectors,
        FeatureUpdateSourceRegistry updateSources,
        ILocalTableProvider localTables,
        string? baseDirectory,
        DiagnosticBag diagnostics)
    {
        _bindings = bindings;
        _connectors = connectors;
        _updateSources = updateSources;
        _localTables = localTables;
        _baseDirectory = baseDirectory;
        _diagnostics = diagnostics;
        _artifact = "bindings:" + bindings.Environment;
    }

    /// <summary>The supply of a source, resolved once per source; null (diagnosed) when its binding is missing or wrong.</summary>
    /// <param name="featurePointer">The catalog pointer of a feature reading the source, for an unbound source.</param>
    public SourceSupply? Source(string sourceName, string featurePointer)
    {
        if (!_resolved.TryGetValue(sourceName, out var supply))
        {
            supply = Resolve(sourceName, featurePointer);
            _resolved[sourceName] = supply;
        }

        return supply;
    }

    /// <summary>
    /// The local table of one (source, entity) group and the table field of each member; null (diagnosed) when the
    /// entity cannot be held locally or the features disagree on a field's type.
    /// </summary>
    public LocalGroupTable? LocalTable(SourceSupply supply, EntityDefinition entity, FeatureRuntimeInfo[] members)
    {
        var pointer = supply.Pointer;
        if (entity.Kind == EntityKind.Cross)
        {
            _diagnostics.Error("LOCAL_CROSS_ENTITY", _artifact, pointer + "/mode",
                $"Source '{supply.Name}' is bound with mode 'local', but feature '{members[0].Definition.Name}' reads it for cross entity '{entity.Name}'. " +
                LocalTableSchema.CrossEntityMessage(entity.Name), "bind the source with mode 'fetch'");
            return null;
        }

        var definitions = new FeatureDefinition[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            definitions[i] = members[i].Definition;
        }

        if (!LocalTableSchema.TryFromFeatures(supply.Name, entity, LocalTableMode.Local, definitions, out var schema, out var conflict))
        {
            _diagnostics.Error("LOCAL_FIELD_TYPE_CONFLICT", "catalog", ConflictPointer(members, conflict!), conflict!,
                "give every feature that reads the field the same type, or read it through a derived feature");
            return null;
        }

        // One update source per table: a source may be stateful (a stream consumer), so tables never share one.
        IFeatureUpdateSource bootstrap;
        try
        {
            bootstrap = supply.BootstrapFactory!.Create(supply.Name, supply.Binding.Bootstrap!.Settings, _baseDirectory);
        }
        catch (FeatureUpdateException ex)
        {
            ReportSourceSettings(supply.Pointer, ex);
            return null;
        }

        var spec = new LocalTableSpec(schema, supply.Options!, supply.Binding, bootstrap, _artifact, pointer, _baseDirectory);
        LocalFeatureTable table;
        try
        {
            table = _localTables.Acquire(spec);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // max_bytes below what an empty table takes.
            _diagnostics.Error("LOCAL_MAX_BYTES", _artifact, pointer + "/max_bytes", FirstLine(ex.Message), "raise max_bytes");
            return null;
        }

        var fields = new int[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            fields[i] = schema.FieldIndex(members[i].Definition.Field!);
        }

        return new LocalGroupTable(spec, table, fields);
    }

    private SourceSupply? Resolve(string sourceName, string featurePointer)
    {
        if (!_bindings.Sources.TryGetValue(sourceName, out var binding))
        {
            _diagnostics.Error("SOURCE_UNBOUND", "catalog", featurePointer,
                $"Feature source '{sourceName}' has no binding in environment '{_bindings.Environment}'.",
                NameSuggestions.DidYouMean(sourceName, _bindings.Sources.Keys) ?? "add it under 'sources' in the bindings artifact");
            return null;
        }

        var pointer = "/sources/" + JsonPointer.Escape(sourceName);
        SupplyMode mode;
        switch (binding.Mode)
        {
            case null or "fetch":
                mode = SupplyMode.Fetch;
                break;
            case "local":
                mode = SupplyMode.Local;
                break;
            default:
                _diagnostics.Error("SOURCE_MODE_UNSUPPORTED", _artifact, pointer + "/mode",
                    $"Supply mode '{binding.Mode}' is not available in this build.", "use mode 'fetch' or 'local'");
                return null;
        }

        // Every problem of the binding at once: settings of the other mode, and what the mode itself requires.
        var settingsOk = CheckModeSettings(sourceName, binding, mode, pointer);
        var supply = mode == SupplyMode.Fetch ? ResolveFetch(sourceName, binding, pointer) : ResolveLocal(sourceName, binding, pointer);
        return settingsOk ? supply : null;
    }

    /// <summary>Reports every setting of the other mode; true when there is none.</summary>
    private bool CheckModeSettings(string sourceName, FeatureSourceBinding binding, SupplyMode mode, string pointer)
    {
        var ok = true;
        foreach (var (name, settingMode, isSet) in ModeSettings)
        {
            if (settingMode != mode && isSet(binding))
            {
                ok = false;
                _diagnostics.Error("SOURCE_SETTING_WRONG_MODE", _artifact, pointer + "/" + name,
                    $"'{name}' is a setting of mode '{ModeName(settingMode)}', but source '{sourceName}' is bound with mode '{ModeName(mode)}'.",
                    $"remove '{name}', or bind the source with \"mode\": \"{ModeName(settingMode)}\"");
            }
        }

        if (mode == SupplyMode.Local)
        {
            foreach (var (name, _) in binding.Settings)
            {
                ok = false;
                _diagnostics.Error("SOURCE_SETTING_WRONG_MODE", _artifact, pointer + "/" + JsonPointer.Escape(name),
                    $"'{name}' is not a setting of mode 'local' (connector settings belong to mode 'fetch'); source '{sourceName}' is bound with mode 'local'.",
                    "a local binding takes 'max_bytes' and 'bootstrap'; settings of the update source go inside 'bootstrap'");
            }
        }

        return ok;
    }

    private SourceSupply? ResolveFetch(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        var type = binding.Connector ?? string.Empty;
        if (!_connectors.TryGet(type, out var factory))
        {
            _diagnostics.Error("CONNECTOR_UNKNOWN", _artifact, pointer + "/connector",
                $"Connector type '{type}' is not registered in this binary.",
                NameSuggestions.DidYouMean(type, _connectors.Types) ?? $"registered: {string.Join(", ", _connectors.Types)}");
            return null;
        }

        IFeatureSourceConnector connector;
        try
        {
            connector = factory.Create(sourceName, binding.Settings, _baseDirectory);
        }
        catch (Exception ex) when (ex is FormatException or IOException or JsonException or InvalidOperationException)
        {
            _diagnostics.Error("CONNECTOR_CONFIG", _artifact, pointer, ex.Message);
            return null;
        }

        return new SourceSupply(sourceName, pointer, binding, SupplyMode.Fetch)
        {
            Connector = connector,
            TimeoutMs = binding.TimeoutMs ?? DefaultTimeoutMs,
            MaxBatch = binding.MaxBatch is > 0 ? binding.MaxBatch.Value : DefaultMaxBatch,
        };
    }

    private SourceSupply? ResolveLocal(string sourceName, FeatureSourceBinding binding, string pointer)
    {
        var ok = true;
        long maxBytes = 0;
        if (binding.MaxBytes is null)
        {
            ok = false;
            _diagnostics.Error("LOCAL_MAX_BYTES", _artifact, pointer, $"Source '{sourceName}' is bound with mode 'local' but has no 'max_bytes'.",
                "add the most memory each of its tables may take, e.g. \"max_bytes\": \"256MiB\"");
        }
        else if (!ByteSize.TryParse(binding.MaxBytes, out maxBytes) || maxBytes == 0)
        {
            ok = false;
            _diagnostics.Error("LOCAL_MAX_BYTES", _artifact, pointer + "/max_bytes", $"'{binding.MaxBytes}' is not a positive size.",
                "write an integer and a unit B, KiB, MiB, GiB or TiB, e.g. \"256MiB\"");
        }

        IFeatureUpdateSourceFactory? factory = null;
        if (binding.Bootstrap is not { } bootstrap)
        {
            ok = false;
            _diagnostics.Error("LOCAL_BOOTSTRAP_MISSING", _artifact, pointer, $"Source '{sourceName}' is bound with mode 'local' but has no 'bootstrap'.",
                $"add the update source that loads it, e.g. \"bootstrap\": {{ \"source\": \"{SnapshotFilesUpdateSource.Type}\", \"uri\": \"<snapshot directory>\" }}");
        }
        else if (!_updateSources.TryGet(bootstrap.Source ?? string.Empty, out factory))
        {
            ok = false;
            var type = bootstrap.Source ?? string.Empty;
            _diagnostics.Error("UPDATE_SOURCE_UNKNOWN", _artifact, pointer + "/bootstrap" + (bootstrap.Source is null ? string.Empty : "/source"),
                bootstrap.Source is null ? "The bootstrap names no 'source'." : $"Update source type '{type}' is not registered in this binary.",
                NameSuggestions.DidYouMean(type, _updateSources.Types) ?? $"registered: {string.Join(", ", _updateSources.Types)}");
        }

        // The bootstrap settings are checked once for the source (not per entity), and even when the rest is wrong, so
        // every problem of the binding is reported at once.
        if (factory is not null)
        {
            try
            {
                _ = factory.Create(sourceName, binding.Bootstrap!.Settings, _baseDirectory);
            }
            catch (FeatureUpdateException ex)
            {
                ok = false;
                ReportSourceSettings(pointer, ex);
            }
        }

        if (!ok)
        {
            return null;
        }

        return new SourceSupply(sourceName, pointer, binding, SupplyMode.Local)
        {
            BootstrapFactory = factory,
            Options = new LocalFeatureTableOptions { MaxBytes = maxBytes },
            TimeoutMs = DefaultTimeoutMs,
            MaxBatch = LocalMaxBatch,
        };
    }

    private void ReportSourceSettings(string pointer, FeatureUpdateException ex) =>
        _diagnostics.Error("UPDATE_SOURCE_CONFIG", _artifact, pointer + "/bootstrap" + ex.Pointer, ex.Reason, ex.Hint);

    /// <summary>The pointer of the conflicting feature's type: the conflict message names the feature first.</summary>
    private static string ConflictPointer(FeatureRuntimeInfo[] members, string conflict)
    {
        foreach (var member in members)
        {
            if (conflict.StartsWith($"Feature '{member.Definition.Name}'", StringComparison.Ordinal))
            {
                return member.Definition.Pointer + "/type";
            }
        }

        return members[0].Definition.Pointer;
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOf('\n', StringComparison.Ordinal);
        var line = end < 0 ? message : message[..end];
        var parameter = line.IndexOf(" (Parameter", StringComparison.Ordinal);
        return parameter < 0 ? line : line[..parameter];
    }

    internal static string ModeName(SupplyMode mode) => mode == SupplyMode.Local ? "local" : "fetch";
}

/// <summary>A resolved feature source binding: its mode and what that mode needs.</summary>
internal sealed class SourceSupply(string name, string pointer, FeatureSourceBinding binding, SupplyMode mode)
{
    public string Name { get; } = name;

    /// <summary>The binding's pointer, e.g. <c>/sources/item_store</c>.</summary>
    public string Pointer { get; } = pointer;

    public FeatureSourceBinding Binding { get; } = binding;

    public SupplyMode Mode { get; } = mode;

    /// <summary>Mode fetch.</summary>
    public IFeatureSourceConnector? Connector { get; init; }

    public int TimeoutMs { get; init; }

    public int MaxBatch { get; init; }

    /// <summary>Mode local: the factory of the bootstrap source.</summary>
    public IFeatureUpdateSourceFactory? BootstrapFactory { get; init; }

    /// <summary>Mode local: the options of every table of the source.</summary>
    public LocalFeatureTableOptions? Options { get; init; }
}
