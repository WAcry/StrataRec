using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StrataRec.Artifacts.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Experiments.Compilation;

/// <summary>
/// Compiles a pipeline and its experiments into one unified <see cref="ExecutionPlan"/>: parameter
/// snapshots for parameter-only arms, precompiled node variants for every arm combination of the layers that touch a
/// node, timeline positions for added, deleted and moved nodes, and trigger sites.
/// </summary>
/// <remarks>
/// With no experiments this is the plain pipeline compiler. <see cref="CompileMerged"/> compiles baseline ⊕ a set of
/// arms as a plain pipeline; it is the reference the unified plan is differentially tested against.
/// </remarks>
public static class PlanCompiler
{
    /// <summary>Where diagnostics of a configuration override itself point: the request (<see cref="CompileMerged"/>).</summary>
    public const string OverrideArtifact = "request";

    /// <summary>The override's place in a request file (<c>stratarec run</c>); the paths below it are those of the patch.</summary>
    public const string OverridePointer = "/config_override";

    /// <summary>
    /// How deep a configuration override may nest objects and arrays, the override itself being the first level; deeper is
    /// <c>CONFIG_OVERRIDE_INVALID</c> at the first level too deep. A recording holds the override two levels down
    /// (<c>/request/config_override</c>) in a file written and read at most <see cref="Jsonc.MaxDepth"/> levels deep, as
    /// every configuration file is: so every override that runs can be recorded and replayed from its file, and a request
    /// file (<c>stratarec run</c>, one level down) holds any. Before the limit, a host's override nested deeper than a
    /// reader's default depth threw out of the request where it was checked.
    /// </summary>
    public const int OverrideMaxDepth = Jsonc.MaxDepth - 2;

    /// <param name="bindings">
    /// Where the plan's local feature tables, clients and connectors come from: the engine passes the
    /// plan's binding lease, which shares and loads them. Null gives ones that are never loaded, for compiling without
    /// serving (validate, build, explain): requests reading such a table fail the features, and model signatures that
    /// need a loaded client are checked when an engine loads the plan.
    /// </param>
    public static PlanCompilation Compile(PlanCompileRequest request, PlanEnvironment? environment = null, IPlanBindings? bindings = null)
    {
        var started = Stopwatch.GetTimestamp();
        var view = PatchableView.Create(request.Pipeline, request.Catalog);
        var compilation = new UnifiedCompilation(request, environment ?? new PlanEnvironment(), view, new DiagnosticBag(), buildPlan: true,
            bindings ?? new UnloadedBindings()).Run();
        StrataRecMetrics.PlanCompileDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>(StrataRecTags.Pipeline, request.Pipeline.PipelineId));
        return compilation;
    }

    private static bool YieldsToAny(ExperimentPlacement placement, HashSet<string> experiments)
    {
        foreach (var to in placement.YieldsTo)
        {
            if (to != placement.Experiment.ExperimentId && experiments.Contains(to))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compiles the baseline with the patches of <paramref name="armIds"/> (<c>experiment/arm</c>) applied in layer
    /// order, as a plain pipeline without experiments. Running it equals running the unified plan with those arms: an
    /// arm whose experiment yields to the experiment of another of them is left out, as a request runs without it.
    /// </summary>
    /// <param name="configOverride">
    /// A request's configuration override, applied after the arms, so it wins over them: an RFC 7396 merge
    /// patch of the patchable view (<see cref="PatchableView.Sections"/>), checked as the baseline is, not by the
    /// experiment policy. Its own mistakes are reported at <see cref="OverrideArtifact"/>, <see cref="OverridePointer"/>,
    /// and so is what the compile finds at a value it sets or within an object it patches (<see cref="InOverride"/>). It is
    /// read once, by its check, and compiled as read (<see cref="PlanCompilation.ConfigOverride"/>).
    /// </param>
    public static PlanCompilation CompileMerged(
        PlanCompileRequest request, IReadOnlyList<string> armIds, PlanEnvironment? environment = null, IPlanBindings? bindings = null,
        JsonObject? configOverride = null)
    {
        var diagnostics = new DiagnosticBag();
        var view = PatchableView.Create(request.Pipeline, request.Catalog);
        var selected = new List<(string Layer, string Id, JsonObject Patch)>();
        foreach (var id in armIds)
        {
            var found = false;
            foreach (var placement in request.Experiments)
            {
                var e = placement.Experiment;
                if (id.StartsWith(e.ExperimentId + "/", StringComparison.Ordinal)
                    && e.Arms.TryGetValue(id[(e.ExperimentId.Length + 1)..], out var patch))
                {
                    selected.Add((placement.Layer, id, patch));
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException($"Unknown arm '{id}'. Arms are named 'experiment/arm'.", nameof(armIds));
            }
        }

        // As the unified plan runs them (ArmYields): an arm whose experiment yields to the experiment of another arm here
        // does not run. Judged on the arms given, not on those left, so the order does not matter.
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, id, _) in selected)
        {
            present.Add(id[..id.IndexOf('/')]);
        }

        for (var i = selected.Count - 1; i >= 0; i--)
        {
            var experiment = selected[i].Id[..selected[i].Id.IndexOf('/')];
            foreach (var placement in request.Experiments)
            {
                if (placement.Experiment.ExperimentId == experiment && YieldsToAny(placement, present))
                {
                    selected.RemoveAt(i);
                    break;
                }
            }
        }

        selected.Sort(static (a, b) => string.CompareOrdinal(a.Layer, b.Layer));
        foreach (var (_, id, patch) in selected)
        {
            var merged = view.Apply(patch, diagnostics, "experiment:" + id[..id.IndexOf('/')], "/arms/" + id[(id.IndexOf('/') + 1)..]);
            if (merged is null)
            {
                return new PlanCompilation { Diagnostics = diagnostics.Items, Report = UnifiedCompilation.EmptyReport };
            }

            view = merged;
        }

        JsonObject? checkedOverride = null;
        if (configOverride is not null)
        {
            // A host may build the override from text that repeats a member (JsonNode.Parse keeps both, and the object throws
            // when it is first enumerated, also under a case-insensitive comparison), nest it as deep as it likes, or wrap a
            // CLR object that writes either (JsonValue.Create): the override's mistake, at its pointer, found before anything
            // else reads it, not an exception out of the request. Everything below reads the copy the
            // check made, never the host's tree, which would write its CLR values again, perhaps differently.
            if (StrictJson.Check(configOverride, OverrideMaxDepth, out var read) is { } invalid)
            {
                diagnostics.Error("CONFIG_OVERRIDE_INVALID", OverrideArtifact, OverridePointer + invalid.Pointer, invalid.Message, invalid.Hint);
                return new PlanCompilation { Diagnostics = diagnostics.Items, Report = UnifiedCompilation.EmptyReport };
            }

            checkedOverride = read!.AsObject();
            var known = true;
            foreach (var (section, _) in checkedOverride)
            {
                if (Array.IndexOf(PatchableView.Sections, section) < 0)
                {
                    diagnostics.Error("CONFIG_OVERRIDE_UNKNOWN_SECTION", OverrideArtifact, OverridePointer + "/" + JsonPointer.Escape(section),
                        $"'{section}' is not a section a configuration override can patch.",
                        $"patch one of {string.Join(", ", PatchableView.Sections)}, as an experiment arm does");
                    known = false;
                }
            }

            // The compiler hashes the definitions it builds (RFC 8785): a value of the override that form cannot hold
            // exactly (an integer beyond 2^53) is the override's mistake, at its pointer, not an exception out of the request.
            try
            {
                JsonCanonicalizer.CanonicalizeToString(checkedOverride);
            }
            catch (JsonCanonicalizationException ex)
            {
                diagnostics.Error("CONFIG_OVERRIDE_NOT_CANONICAL", OverrideArtifact, OverridePointer + ex.Pointer.ToString(), ex.Reason,
                    ex.Hint ?? "write the value so that RFC 8785 holds it exactly");
                known = false;
            }

            if (!known || view.Apply(checkedOverride, diagnostics, OverrideArtifact, OverridePointer) is not { } overridden)
            {
                return new PlanCompilation { Diagnostics = diagnostics.Items, Report = UnifiedCompilation.EmptyReport };
            }

            view = overridden;
        }

        var plain = UnifiedCompilation.WithoutExperiments(request);
        var start = diagnostics.Count;
        var compilation = new UnifiedCompilation(plain, environment ?? new PlanEnvironment(), view, diagnostics, buildPlan: true, bindings ?? new UnloadedBindings()).Run();
        if (checkedOverride is not null && diagnostics.Count > start)
        {
            // The compilation's diagnostics are the bag's: moved in place, they are what it reports.
            var pipeline = "pipeline:" + request.Pipeline.PipelineId;
            var catalog = "catalog:" + request.Catalog.CatalogId;
            foreach (var d in diagnostics.RemoveFrom(start))
            {
                diagnostics.Add((d.Artifact == pipeline || d.Artifact == catalog) && InOverride(checkedOverride, d.Pointer)
                    ? d with { Artifact = OverrideArtifact, Pointer = OverridePointer + d.Pointer }
                    : d);
            }
        }

        compilation.ConfigOverride = checkedOverride;
        return compilation;
    }

    /// <summary>
    /// Whether <paramref name="pointer"/>, a location in the patchable view (where the pipeline's and the catalog's
    /// sections are), is one <paramref name="configOverride"/> writes: at or under a value it sets (a scalar, an array,
    /// null), or an object it patches into. The compile of the merged configuration reports what it finds at the
    /// pipeline's or catalog's pointer, though the value came from the override: an integer the canonical form cannot tell
    /// from a float (<c>-90071992547409930E-1</c>) passed the override's own checks and was refused by the parameter's
    /// binding at <c>pipeline:home_feed/params/big</c>, with no pointer for the caller, while the same integer written as
    /// digits was refused at <c>/config_override/params/big</c>. Such a diagnostic keeps its code, message and
    /// hint and moves to the override, as an arm's moves to the arm whose patch touches it. Names are compared by ordinal,
    /// as RFC 7396 merging compares them (<see cref="JsonMergePatch"/>): a <c>FINAL_TOP_K</c> the override sets is another
    /// parameter than the baseline's <c>final_top_k</c>, whatever the host's object compares (one parsed with
    /// <see cref="JsonNodeOptions.PropertyNameCaseInsensitive"/> moved the baseline's error to the override).
    /// </summary>
    private static bool InOverride(JsonObject configOverride, string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var parsed) || parsed.Depth == 0)
        {
            return false;
        }

        JsonNode? node = configOverride;
        foreach (var token in parsed.Tokens)
        {
            if (node is not JsonObject patched)
            {
                return true; // under a value the override sets whole
            }

            if (!TryGetMember(patched, token, out node))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The member of <paramref name="obj"/> named <paramref name="name"/> by ordinal comparison, as the merge finds it.</summary>
    private static bool TryGetMember(JsonObject obj, string name, out JsonNode? value)
    {
        foreach (var (key, member) in obj)
        {
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                value = member;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// What a release of <paramref name="request"/> needs of a binary: the baseline's needs, and for each
    /// experiment what it adds, so that every set of experiments left after revocations requires at least what it needs. No
    /// plan is built and nothing is loaded. Null after reporting the errors of a compile that failed; its warnings are the
    /// full compile's to report.
    /// </summary>
    /// <remarks>
    /// <para>Recording each experiment's needs is what lets a revocation, which compiles nothing, require what is left:
    /// inferring them back from the configuration's names kept what an operator used by default and freed nothing a feature
    /// source needed.</para>
    /// <para>A combination can need what no experiment needs alone: one arm points recall at a quota map, an arm of another
    /// layer adds a source of a new client type to that map; and what one experiment needs alone, another pair
    /// can need too. So each capability the release needs beyond the baseline goes to: the experiments that need
    /// it alone (O); and, if the release without O still needs it, each remaining experiment without which those remaining
    /// no longer need it (one compile without each). If none such exists, it stays with the baseline. Compiling more
    /// experiments never needs less (every arm's variant joins the baseline's), so a set without a holder needs it no more
    /// than the release without O, or without that holder, does: what is left always requires what it needs. Too much is
    /// possible (a need no single removal ends goes to the baseline), too little is not.</para>
    /// </remarks>
    public static ReleaseRequirements? Requirements(PlanCompileRequest request, PlanEnvironment? environment, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var compiles = new RequirementCompiles(request, environment ?? new PlanEnvironment(), diagnostics);
        var all = new List<ExperimentPlacement>(request.Experiments);
        if (compiles.Needs([]) is not { } baselineNeeds)
        {
            return null;
        }

        var baseline = new SortedSet<string>(baselineNeeds, StringComparer.Ordinal);
        var alone = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var placement in all)
        {
            if (compiles.Needs([placement]) is not { } needs)
            {
                return null;
            }

            var own = new SortedSet<string>(needs, StringComparer.Ordinal);
            own.ExceptWith(baseline);
            alone[placement.Experiment.ExperimentId] = own;
            extra.UnionWith(own);
        }

        if (all.Count > 1)
        {
            if (compiles.Needs(all) is not { } full)
            {
                return null;
            }

            extra.UnionWith(full);
            extra.ExceptWith(baseline);
        }

        var requires = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var placement in all)
        {
            requires[placement.Experiment.ExperimentId] = new SortedSet<string>(StringComparer.Ordinal);
        }

        foreach (var capability in extra)
        {
            var holders = new List<ExperimentPlacement>();
            var rest = new List<ExperimentPlacement>();
            foreach (var placement in all)
            {
                (alone[placement.Experiment.ExperimentId].Contains(capability) ? holders : rest).Add(placement);
            }

            if (compiles.Needs(rest) is not { } restNeeds)
            {
                return null;
            }

            if (restNeeds.Contains(capability))
            {
                // A combination of the rest needs it too: each of them without which the rest no longer does.
                var ends = false;
                foreach (var placement in rest)
                {
                    if (compiles.Needs(Without(rest, placement)) is not { } without)
                    {
                        return null;
                    }

                    if (!without.Contains(capability))
                    {
                        holders.Add(placement);
                        ends = true;
                    }
                }

                if (!ends)
                {
                    baseline.Add(capability);
                    continue;
                }
            }

            foreach (var holder in holders)
            {
                requires[holder.Experiment.ExperimentId].Add(capability);
            }
        }

        var experiments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (id, needs) in requires)
        {
            experiments[id] = new List<string>(needs);
        }

        return new ReleaseRequirements(new List<string>(baseline), experiments);
    }

    private static List<ExperimentPlacement> Without(List<ExperimentPlacement> placements, ExperimentPlacement removed)
    {
        var rest = new List<ExperimentPlacement>(placements.Count - 1);
        foreach (var placement in placements)
        {
            if (!ReferenceEquals(placement, removed))
            {
                rest.Add(placement);
            }
        }

        return rest;
    }

    /// <summary>The needs of compiles of the baseline with sets of experiments, each set compiled once.</summary>
    private sealed class RequirementCompiles(PlanCompileRequest request, PlanEnvironment environment, DiagnosticBag diagnostics)
    {
        private readonly PatchableView _view = PatchableView.Create(request.Pipeline, request.Catalog);
        private readonly Dictionary<string, SortedSet<string>> _needs = new(StringComparer.Ordinal);

        /// <summary>What the release with <paramref name="placements"/> needs; null after reporting a compile's errors.</summary>
        public SortedSet<string>? Needs(List<ExperimentPlacement> placements)
        {
            var key = new System.Text.StringBuilder();
            foreach (var placement in placements)
            {
                key.Append(placement.Experiment.ExperimentId).Append('\n');
            }

            if (_needs.TryGetValue(key.ToString(), out var known))
            {
                return known;
            }

            var compile = new DiagnosticBag();
            var compiled = placements.Count == 0 ? UnifiedCompilation.WithoutExperiments(request) : UnifiedCompilation.With(request, placements);
            var compilation = new UnifiedCompilation(compiled, environment, _view, compile, buildPlan: false, new UnloadedBindings()).Run();
            if (compile.HasErrors)
            {
                foreach (var d in compile.Items)
                {
                    if (d.IsError)
                    {
                        diagnostics.Add(d);
                    }
                }

                return null;
            }

            var needs = new SortedSet<string>(compilation.Requires, StringComparer.Ordinal);
            _needs[key.ToString()] = needs;
            return needs;
        }
    }
}

/// <summary>The state of one compile. Not thread-safe; one instance per compile.</summary>
internal sealed partial class UnifiedCompilation
{
    internal static readonly PlanReport EmptyReport = new() { Nodes = Array.Empty<NodeVariantReport>(), Arms = Array.Empty<ArmReport>() };

    private readonly PlanCompileRequest _request;
    private readonly PlanEnvironment _environment;
    private readonly PatchableView _view;
    private readonly DiagnosticBag _diagnostics;
    private readonly bool _buildPlan;
    private readonly IPlanBindings _bindings;
    private readonly PlanBuilder _builder;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly string _pipelineLabel;

    private readonly List<string> _layers = new();
    private readonly List<(string Id, int Layer, ImmutableArray<int> Arms)> _experiments = new();

    /// <summary>Experiment id → its artifact label and its <see cref="ExperimentArtifact.ReviewedInteractions"/>.</summary>
    private readonly Dictionary<string, (string Artifact, Dictionary<string, List<string>> Columns)> _reviewed = new(StringComparer.Ordinal);

    /// <summary>The reviewed interactions an interaction matched: (experiment, other experiment, column).</summary>
    private readonly HashSet<(string, string, string)> _reviewUsed = new();

    /// <summary>Which experiments yield to which (<see cref="ArmYields"/>); every combination is judged as it runs.</summary>
    private ArmYields _yields = ArmYields.None;

    /// <summary>Each declared yield of a present experiment to another, where it is declared.</summary>
    private readonly List<(int From, int To, string Release, string Pointer)> _yieldDeclarations = new();

    /// <summary>Per pair of experiments (lower index first) that never run together because one yields: what that resolves.</summary>
    private readonly SortedDictionary<(int, int), SortedSet<string>> _yieldResolves = new();
    private readonly List<ArmPatch> _arms = new();
    private readonly SortedDictionary<string, NodeVariants> _variants = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, TapVariants> _taps = new(StringComparer.Ordinal);

    /// <summary>Union of add-only additions: section → name → (value, first arm).</summary>
    private readonly Dictionary<string, SortedDictionary<string, (JsonNode Value, ArmPatch Arm)>> _additions = new(StringComparer.Ordinal);

    /// <param name="bindings">One for the whole compile, its validation passes included: they compile the same nodes, so
    /// they share its clients and signatures. Only a compilation that builds the plan builds the feature plan, so only it
    /// acquires tables and connectors.</param>
    public UnifiedCompilation(
        PlanCompileRequest request, PlanEnvironment environment, PatchableView view, DiagnosticBag diagnostics, bool buildPlan, IPlanBindings bindings)
    {
        _request = request;
        _environment = environment;
        _view = view;
        _diagnostics = diagnostics;
        _buildPlan = buildPlan;
        _bindings = bindings;
        _builder = new PlanBuilder(environment, diagnostics, bindings);
        _pipelineLabel = "pipeline:" + request.Pipeline.PipelineId;
    }

    /// <summary><paramref name="r"/> with some of its experiments, checked as <see cref="WithoutExperiments"/> is.</summary>
    public static PlanCompileRequest With(PlanCompileRequest r, IReadOnlyList<ExperimentPlacement> experiments) => new()
    {
        Pipeline = r.Pipeline,
        Catalog = r.Catalog,
        Bindings = r.Bindings,
        Tables = r.Tables,
        Policy = r.Policy,
        Pipelines = r.Pipelines,
        Experiments = experiments,
        ManifestId = r.ManifestId,
        ValidateArmsIndependently = false,
        MaxCombinationsPerNode = r.MaxCombinationsPerNode,
        MaxCombinationsChecked = r.MaxCombinationsChecked,
    };

    public static PlanCompileRequest WithoutExperiments(PlanCompileRequest r) => new()
    {
        Pipeline = r.Pipeline,
        Catalog = r.Catalog,
        Bindings = r.Bindings,
        Tables = r.Tables,
        Policy = r.Policy,
        Pipelines = r.Pipelines,
        ManifestId = r.ManifestId,
        ValidateArmsIndependently = false,
        MaxCombinationsPerNode = r.MaxCombinationsPerNode,
        MaxCombinationsChecked = r.MaxCombinationsChecked,
        Today = r.Today,
    };

    public PlanCompilation Run()
    {
        var start = _diagnostics.Count;
        DefineExperiments();
        DefineYields();
        Settle(start, Array.Empty<ArmPatch>());

        start = _diagnostics.Count;
        var sources = BuildSchemaSources();
        _builder.DefineSchema(sources);
        SettleAdditions(start);

        start = _diagnostics.Count;
        var parameters = BuildParams(out var leafExperiments);
        Settle(start, Array.Empty<ArmPatch>());

        CompileVariants();

        // After the nodes: operators declare implicit columns (e.g. recall.*) that the output may read.
        start = _diagnostics.Count;
        var output = _builder.CompileOutput();
        Settle(start, Array.Empty<ArmPatch>());
        CompileTapVariants();
        var (timeline, fallbacks) = BuildSlots();

        var report = new CombinationReport();
        if (output is not null)
        {
            ValidateCombinations(timeline, fallbacks, output, report);
        }

        ValidatePairs();
        ValidateYields();
        ValidateReviewedInteractions();
        ValidateBaselineAlone();
        if (_request.ValidateArmsIndependently)
        {
            ValidateArmsIndependently();
        }

        start = _diagnostics.Count;
        AssignTriggerSites(timeline, fallbacks, output, leafExperiments);
        Settle(start, Array.Empty<ArmPatch>());

        ExecutionPlan? plan = null;
        if (_buildPlan)
        {
            start = _diagnostics.Count;
            var features = _builder.BuildFeaturePlan();
            SettleAdditions(start);
            if (!_diagnostics.HasErrors && output is not null)
            {
                plan = _builder.Build(_request.ManifestId, features, timeline, fallbacks, output, parameters, LayerInfos(), ExperimentInfos(), ArmInfos(), BuildLogPlan());
                plan = _diagnostics.HasErrors ? null : plan;
            }

            _builder.ReportDeprecatedReads(_request.Pipeline.PipelineId);
        }

        return new PlanCompilation
        {
            Plan = plan,
            Diagnostics = _diagnostics.Items,
            Requires = new List<string>(_builder.RequiredCapabilities()),
            Report = new PlanReport
            {
                Nodes = NodeReports(),
                Arms = ArmReports(),
                CombinationsChecked = report.Checked,
                CombinationCheckExhaustive = report.Exhaustive,
                Yields = YieldReports(),
            },
        };
    }

    // ------------------------------------------------------------------ experiments and arms

    private void DefineExperiments()
    {
        var placements = new List<ExperimentPlacement>(_request.Experiments);
        placements.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Layer, b.Layer);
            return c != 0 ? c : string.CompareOrdinal(a.Experiment.ExperimentId, b.Experiment.ExperimentId);
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placement in placements)
        {
            var e = placement.Experiment;
            var artifact = "experiment:" + e.ExperimentId;
            if (!seen.Add(e.ExperimentId))
            {
                _diagnostics.Error("EXPERIMENT_DUPLICATE", artifact, "/experiment_id", $"Experiment '{e.ExperimentId}' is listed twice.");
                continue;
            }

            if (!ArmAnalyzer.IsValidId(e.ExperimentId))
            {
                _diagnostics.Error("BAD_ID", artifact, "/experiment_id", $"'{e.ExperimentId}' is not a valid experiment id.",
                    "ids start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
            }

            if (!string.Equals(e.PipelineId, _request.Pipeline.PipelineId, StringComparison.Ordinal))
            {
                _diagnostics.Error("EXPERIMENT_WRONG_PIPELINE", artifact, "/pipeline_id",
                    $"Experiment targets pipeline '{e.PipelineId}', not '{_request.Pipeline.PipelineId}'.");
            }

            if (!DateOnly.TryParseExact(e.ExpiresAt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires))
            {
                _diagnostics.Error("EXPERIMENT_BAD_EXPIRY", artifact, "/expires_at", $"'{e.ExpiresAt}' is not a date.", "use yyyy-MM-dd");
            }
            else if (_request.Today is { } today && expires < today)
            {
                _diagnostics.Error("EXPERIMENT_EXPIRED", artifact, "/expires_at", $"Experiment expired on {e.ExpiresAt}.",
                    "graduate the winning arm into the baseline or remove the experiment");
            }
            else if (_request.Today is { } now && expires.DayNumber - now.DayNumber <= PlanCompileRequest.ExpiryWarningDays)
            {
                var days = expires.DayNumber - now.DayNumber;
                _diagnostics.Warning("EXPERIMENT_EXPIRING", artifact, "/expires_at",
                    $"Experiment expires on {e.ExpiresAt}, {(days == 0 ? "today" : days == 1 ? "in 1 day" : $"in {days} days")}; after that it cannot be built into a release.",
                    "graduate the winning arm into the baseline, remove the experiment, or move expires_at");
            }

            if (e.Arms.Count == 0)
            {
                _diagnostics.Error("EXPERIMENT_NO_ARMS", artifact, "/arms", "An experiment needs at least one arm.");
                continue;
            }

            var layer = _layers.IndexOf(placement.Layer);
            if (layer < 0)
            {
                layer = _layers.Count;
                _layers.Add(placement.Layer);
            }

            var experimentIndex = _experiments.Count;
            var armNames = new List<string>(e.Arms.Keys);
            armNames.Sort(StringComparer.Ordinal);
            var armIndices = new int[armNames.Count];
            for (var i = 0; i < armNames.Count; i++)
            {
                var name = armNames[i];
                var arm = new ArmPatch
                {
                    Index = _arms.Count,
                    Id = e.ExperimentId + "/" + name,
                    ArmName = name,
                    ExperimentIndex = experimentIndex,
                    ExperimentId = e.ExperimentId,
                    LayerIndex = layer,
                    Patch = e.Arms[name],
                    Artifact = artifact,
                    PointerPrefix = "/arms/" + JsonPointer.Escape(name),
                };
                if (!ArmAnalyzer.IsValidId(name))
                {
                    _diagnostics.Error("BAD_ID", artifact, arm.PointerPrefix, $"'{name}' is not a valid arm name.",
                        "arm names start with a letter or '_' and contain letters, digits, '_', '.' or '-'");
                    arm.Valid = false;
                }

                ArmAnalyzer.Analyze(arm, _view, _diagnostics);
                armIndices[i] = arm.Index;
                _arms.Add(arm);
            }

            _experiments.Add((e.ExperimentId, layer, ImmutableCollectionsMarshal.AsImmutableArray(armIndices)));
            if (e.ReviewedInteractions is { Count: > 0 } reviewed)
            {
                _reviewed[e.ExperimentId] = (artifact, reviewed);
            }
        }
    }

    /// <summary>
    /// The yields of the release (<see cref="ExperimentPlacement.YieldsTo"/>): an experiment yields only to
    /// another of this compile in another layer, never to itself, and never back along a chain of yields.
    /// </summary>
    private void DefineYields()
    {
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _experiments.Count; i++)
        {
            byId[_experiments[i].Id] = i;
        }

        var yieldsTo = new SortedSet<int>?[_experiments.Count];
        var declared = new Dictionary<(int, int), (string Release, string Pointer)>();
        foreach (var placement in _request.Experiments)
        {
            var id = placement.Experiment.ExperimentId;
            if (!byId.TryGetValue(id, out var from))
            {
                continue; // a duplicate or an experiment without arms: reported where it was defined
            }

            for (var i = 0; i < placement.YieldsTo.Count; i++)
            {
                var name = placement.YieldsTo[i];
                var pointer = "/experiments/" + JsonPointer.Escape(id) + "/yields_to/" + i;
                if (name is null || !byId.TryGetValue(name, out var to))
                {
                    continue; // not in this compile: a release that stopped it yields nothing to it (ExperimentPlacement.YieldsTo)
                }

                if (to == from)
                {
                    _diagnostics.Error("YIELD_SELF", placement.Release, pointer, $"'{id}' yields to itself.", "remove the entry");
                }
                else if (_experiments[to].Layer == _experiments[from].Layer)
                {
                    _diagnostics.Warning("YIELD_SAME_LAYER", placement.Release, pointer,
                        $"'{id}' yields to '{name}', but both are in layer '{_layers[_experiments[from].Layer]}': a request is never in both.",
                        "remove the entry (arms of one layer never run together)");
                }
                else if ((yieldsTo[from] ??= new SortedSet<int>()).Add(to))
                {
                    declared[(from, to)] = (placement.Release, pointer);
                    _yieldDeclarations.Add((from, to, placement.Release, pointer));
                }
            }
        }

        // A cycle (a yields to b, b to a) has no experiment that runs where all are assigned, and no meaning as a decision.
        var state = new byte[_experiments.Count]; // 0 unvisited, 1 on the path, 2 done
        var path = new List<int>();
        for (var start = 0; start < _experiments.Count; start++)
        {
            FindCycles(start);
        }

        var arrays = new ImmutableArray<int>[_experiments.Count];
        for (var i = 0; i < arrays.Length; i++)
        {
            arrays[i] = yieldsTo[i] is { } set ? [.. set] : [];
        }

        var experimentOfArm = new int[_arms.Count];
        foreach (var arm in _arms)
        {
            experimentOfArm[arm.Index] = arm.ExperimentIndex;
        }

        var layerOfExperiment = new int[_experiments.Count];
        for (var i = 0; i < layerOfExperiment.Length; i++)
        {
            layerOfExperiment[i] = _experiments[i].Layer;
        }

        _yields = ArmYields.Create(experimentOfArm, layerOfExperiment, arrays);

        void FindCycles(int experiment)
        {
            if (state[experiment] == 2)
            {
                return;
            }

            state[experiment] = 1;
            path.Add(experiment);
            foreach (var to in yieldsTo[experiment] ?? [])
            {
                if (state[to] == 1)
                {
                    var names = new List<string>();
                    for (var i = path.IndexOf(to); i < path.Count; i++)
                    {
                        names.Add(_experiments[path[i]].Id);
                    }

                    names.Add(_experiments[to].Id);
                    var (release, pointer) = declared[(experiment, to)];
                    _diagnostics.Error("YIELD_CYCLE", release, pointer,
                        $"Yields go round in a cycle: {string.Join(" yields to ", names)}.",
                        "a yield gives one experiment priority over another: remove one of these entries");
                }
                else
                {
                    FindCycles(to);
                }
            }

            path.RemoveAt(path.Count - 1);
            state[experiment] = 2;
        }
    }

    // ------------------------------------------------------------------ schema

    private SchemaSources BuildSchemaSources()
    {
        foreach (var section in ArmAnalyzer.SchemaSections)
        {
            _additions[section] = new SortedDictionary<string, (JsonNode, ArmPatch)>(StringComparer.Ordinal);
        }

        var extraLeaves = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var arm in _arms)
        {
            if (!arm.Valid)
            {
                continue;
            }

            foreach (var leaf in arm.ExtraParamLeaves)
            {
                extraLeaves.Add(leaf);
            }

            foreach (var (section, entries) in arm.Additions)
            {
                if (!_additions.TryGetValue(section, out var union))
                {
                    continue;
                }

                foreach (var (name, (value, pointer)) in entries)
                {
                    if (!union.TryGetValue(name, out var existing))
                    {
                        union[name] = (value, arm);
                    }
                    else if (!ArmAnalyzer.JsonEquals(existing.Value, value))
                    {
                        _diagnostics.Error("ADD_CONFLICT", arm.Artifact, pointer,
                            $"'/{section}/{name}' is also added by '{existing.Arm.Id}' with a different definition.",
                            "an added name has one meaning everywhere: use identical definitions or different names");
                    }
                }
            }
        }

        var columns = ReadAdditions<ColumnSpec>("columns");
        var requestColumns = ReadAdditions<ColumnSpec>("request_columns");
        var paramSchema = ReadAdditions<ParamSpec>("param_schema");
        var features = new Dictionary<string, FeatureSpec>(_view.Catalog.Features, StringComparer.Ordinal);
        foreach (var (name, spec) in ReadAdditions<FeatureSpec>("features"))
        {
            features[name] = spec;
        }

        var derived = new Dictionary<string, DerivedFeatureSpec>(_view.Catalog.DerivedFeatures, StringComparer.Ordinal);
        foreach (var (name, spec) in ReadAdditions<DerivedFeatureSpec>("derived_features"))
        {
            derived[name] = spec;
        }

        var funnelGroups = ReadAdditions<FunnelGroupSpec>("funnel_groups");

        // Baseline node ids appear in pointers and expressions (col.x@node); keep them well-formed.
        foreach (var (id, _) in _view.Nodes)
        {
            if (!ArmAnalyzer.IsValidNodeId(id))
            {
                _diagnostics.Error("BAD_ID", _pipelineLabel, "/nodes/" + JsonPointer.Escape(id), $"'{id}' is not a valid node id.", ArmAnalyzer.NodeIdHint);
            }
        }

        return new SchemaSources
        {
            Pipeline = _view.Pipeline,
            Catalog = new FeatureCatalogArtifact
            {
                CatalogId = _view.Catalog.CatalogId,
                Description = _view.Catalog.Description,
                Entities = _view.Catalog.Entities,
                Features = features,
                DerivedFeatures = derived,
            },
            Bindings = _request.Bindings,
            Tables = _request.Tables,
            Pipelines = _request.Pipelines,
            ExtraColumns = columns,
            ExtraRequestColumns = requestColumns,
            ExtraParamSchema = paramSchema,
            ExtraParamLeaves = extraLeaves,
            ExtraFunnelGroups = funnelGroups,
        };
    }

    private Dictionary<string, T> ReadAdditions<T>(string section)
        where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, (value, arm)) in _additions[section])
        {
            if (ArtifactSerializer.TryBind<T>(value, $"entry '{name}' of '{section}'", out var entry, out var error))
            {
                result[name] = entry;
            }
            else
            {
                _diagnostics.Error("PATCH_SCHEMA", arm.Artifact, arm.PointerPrefix + "/" + section + "/" + JsonPointer.Escape(name) + error.Pointer,
                    error.Message, error.Hint ?? "a new entry must be a complete definition");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ parameters

    private ParamTable BuildParams(out Dictionary<int, SortedSet<int>> leafExperiments)
    {
        var schema = _builder.Schema;
        var baseline = new ScalarValue[schema.Params.Count];
        for (var i = 0; i < baseline.Length; i++)
        {
            baseline[i] = schema.Params[i].Baseline;
        }

        leafExperiments = new Dictionary<int, SortedSet<int>>();
        var overrides = new ImmutableArray<(int, ScalarValue)>[_arms.Count];
        foreach (var arm in _arms)
        {
            var list = new List<(int, ScalarValue)>();
            foreach (var ov in arm.ParamOverrides)
            {
                if (!schema.TryGetParam(ov.Leaf, out var leaf))
                {
                    // A parameter declared with an error (an unknown type) is reported at its declaration; an invalid
                    // arm's own declarations were dropped with it, and its leaves were checked against them already.
                    if (arm.Valid && !schema.IsUnusable("param." + ov.Leaf))
                    {
                        var declared = new List<string>(schema.Params.Count);
                        foreach (var p in schema.Params)
                        {
                            declared.Add(p.Name);
                        }

                        _diagnostics.Error("PARAM_UNKNOWN", arm.Artifact, ov.Pointer, $"Parameter leaf '{ov.Leaf}' is not declared.",
                            NameSuggestions.DidYouMean(ov.Leaf, declared) ?? "declare it under /param_schema of the pipeline or of this arm");
                    }

                    continue;
                }

                if (_builder.TryParamValue(leaf, ov.Value, arm.Artifact, ov.Pointer, out var value))
                {
                    list.Add((leaf.Id, value));
                    if (!leafExperiments.TryGetValue(leaf.Id, out var set))
                    {
                        set = new SortedSet<int>();
                        leafExperiments[leaf.Id] = set;
                    }

                    set.Add(arm.ExperimentIndex);
                }
            }

            overrides[arm.Index] = [.. list];
        }

        return new ParamTable(ImmutableCollectionsMarshal.AsImmutableArray(baseline), ImmutableCollectionsMarshal.AsImmutableArray(overrides));
    }

    // ------------------------------------------------------------------ node variants

    /// <summary>
    /// The arm combinations of the layers that touch one node or tap: per touching layer, "not
    /// modified" plus each arm that modifies it, indexed mixed-radix exactly like <see cref="TimelineSlot.Select"/>.
    /// </summary>
    internal class VariantSpace
    {
        /// <summary>Touching layers L(n), ascending (patches apply in this order).</summary>
        public required ImmutableArray<int> Layers { get; init; }

        /// <summary>Per touching layer: the arms modifying the item; local index = position + 1.</summary>
        public required int[][] LocalArms { get; init; }

        /// <summary>Per touching layer: global arm index → local index. Shared with the item's slots in the plan.</summary>
        public required ImmutableArray<FrozenDictionary<int, int>> LocalIndex { get; init; }

        public required ImmutableArray<int> Radix { get; init; }

        public int IndexOf(in RequestArms arms)
        {
            var index = 0;
            for (var i = 0; i < Layers.Length; i++)
            {
                var arm = arms.ArmIn(Layers[i]);
                var local = arm >= 0 && LocalIndex[i].TryGetValue(arm, out var l) ? l : 0;
                index = (index * Radix[i]) + local;
            }

            return index;
        }

        /// <summary>
        /// The combination index for a set of arms (one per layer at most). Layers of this space without an arm in the
        /// set, and arms that do not modify the item, map to "not modified".
        /// </summary>
        public int IndexOfArms(IReadOnlyList<ArmPatch> arms)
        {
            var index = 0;
            for (var i = 0; i < Layers.Length; i++)
            {
                var local = 0;
                foreach (var arm in arms)
                {
                    if (arm.LayerIndex == Layers[i] && LocalIndex[i].TryGetValue(arm.Index, out var l))
                    {
                        local = l;
                    }
                }

                index = (index * Radix[i]) + local;
            }

            return index;
        }

        /// <summary>The arms (global indices) a combination index assigns, in layer order.</summary>
        public List<int> ArmsOf(int combination)
        {
            var arms = new List<int>();
            for (var i = Layers.Length - 1; i >= 0; i--)
            {
                var local = combination % Radix[i];
                combination /= Radix[i];
                if (local > 0)
                {
                    arms.Add(LocalArms[i][local - 1]);
                }
            }

            arms.Reverse();
            return arms;
        }
    }

    /// <summary>All arm combinations of the layers that touch one node id, and the variant compiled for each.</summary>
    internal sealed class NodeVariants : VariantSpace
    {
        public required string NodeId { get; init; }

        public required JsonObject?[] Definitions { get; init; }

        public required CompiledNode?[] Variants { get; init; }

        /// <summary>Combinations whose definition failed to compile (errors were reported).</summary>
        public required bool[] Failed { get; init; }

        public bool IsFallback { get; set; }
    }

    /// <summary>Variants of one log tap; taps vary independently of the node they attach to.</summary>
    internal sealed class TapVariants : VariantSpace
    {
        public required string TapId { get; init; }

        public required CompiledTap?[] Variants { get; init; }

        public required bool[] Failed { get; init; }
    }

    private readonly record struct Space(
        ImmutableArray<int> Layers, int[][] LocalArms, ImmutableArray<FrozenDictionary<int, int>> LocalIndex, ImmutableArray<int> Radix, int Count);

    /// <summary>L(x) and its arms for the arms that touch an item; null (after an error) when C(x) exceeds the limit.</summary>
    private Space? BuildSpace(Func<ArmPatch, bool> touches, string what, string pointer)
    {
        var byLayer = new SortedDictionary<int, List<int>>();
        foreach (var arm in _arms)
        {
            if (arm.Valid && touches(arm))
            {
                if (!byLayer.TryGetValue(arm.LayerIndex, out var list))
                {
                    list = new List<int>();
                    byLayer[arm.LayerIndex] = list;
                }

                list.Add(arm.Index);
            }
        }

        var layers = new int[byLayer.Count];
        var localArms = new int[byLayer.Count][];
        var localIndex = new FrozenDictionary<int, int>[byLayer.Count];
        var radix = new int[byLayer.Count];
        long combinations = 1;
        var li = 0;
        foreach (var (layer, arms) in byLayer)
        {
            layers[li] = layer;
            localArms[li] = arms.ToArray();
            var local = new Dictionary<int, int>();
            for (var i = 0; i < arms.Count; i++)
            {
                local[arms[i]] = i + 1;
            }

            localIndex[li] = local.ToFrozenDictionary();

            radix[li] = arms.Count + 1;

            // Saturates before it could overflow: a wrapped product would slip under the limit.
            combinations = combinations > long.MaxValue / radix[li] ? long.MaxValue : combinations * radix[li];
            li++;
        }

        if (combinations > _request.MaxCombinationsPerNode)
        {
            var names = new List<string>();
            foreach (var layer in layers)
            {
                names.Add(_layers[layer]);
            }

            var count = combinations == long.MaxValue ? $"more than {long.MaxValue}" : combinations.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _diagnostics.Error("NODE_TOO_MANY_COMBINATIONS", _pipelineLabel, pointer,
                $"{what} is modified by arms in layers {string.Join(", ", names)}: {count} combinations exceed the limit of {_request.MaxCombinationsPerNode}.",
                "move some of these experiments into the same layer (their arms then exclude each other), or graduate one");
            return null;
        }

        return new Space(
            ImmutableCollectionsMarshal.AsImmutableArray(layers), localArms, ImmutableCollectionsMarshal.AsImmutableArray(localIndex),
            ImmutableCollectionsMarshal.AsImmutableArray(radix), (int)combinations);
    }

    /// <summary>Baseline definition with each assigned arm's <c>/section/id</c> patch applied in layer order.</summary>
    private static JsonNode? Merge(JsonNode? baseline, List<ArmPatch> arms, string section, string id)
    {
        var definition = baseline;
        foreach (var arm in arms)
        {
            if (arm.Patch[section] is JsonObject entries && entries.TryGetPropertyValue(id, out var part))
            {
                definition = JsonMergePatch.Apply(definition, part);
            }
        }

        return definition;
    }

    private void CompileVariants()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in _view.Nodes)
        {
            ids.Add(id);
        }

        foreach (var arm in _arms)
        {
            if (arm.Valid)
            {
                ids.UnionWith(arm.AddedNodes);
            }
        }

        // Each distinct definition of a node is compiled at its own place on the timeline (its order; a fallback, which
        // has none, after the timeline; equal orders by node id): a node declares the columns it writes without a
        // declaration as it compiles (an implicit output: recall.*, GroupAggregate@1's), and a node that runs after it,
        // in any combination, must know them. By id, a reader named before its writer ('author_cap' before
        // 'author_rank') did not know the column; by a node's lowest order, neither did an arm that moves a reader after
        // the writer.
        var timeline = new List<NodeDefinition>();
        foreach (var id in ids)
        {
            if (DefineNodeVariants(id) is { } variants)
            {
                AddDistinctDefinitions(variants, timeline);
            }
        }

        timeline.Sort(static (a, b) =>
            a.Order != b.Order ? a.Order.CompareTo(b.Order)
            : a.Variants != b.Variants ? string.CompareOrdinal(a.Variants.NodeId, b.Variants.NodeId)
            : a.Combinations[0].CompareTo(b.Combinations[0]));
        foreach (var definition in timeline)
        {
            var variants = definition.Variants;
            var first = definition.Combinations[0];
            var start = _diagnostics.Count;
            var compiled = _builder.CompileNode(variants.NodeId, variants.Definitions[first]!, _pipelineLabel);
            foreach (var c in definition.Combinations)
            {
                variants.Variants[c] = compiled;
                variants.Failed[c] = compiled is null;
            }

            Settle(start, Running(ArmPatches(variants.ArmsOf(first))));
        }
    }

    /// <summary>
    /// A node definition's order as it binds (<see cref="NodeSpec.Order"/>): an integer JSON number, however the tree holds
    /// it. A tree built in code holds an <c>int</c> where a parsed one holds a JSON element, and reading only one CLR type
    /// put a moved writer last on the timeline, after the reader it declares a column for.
    /// </summary>
    internal static int? OrderOf(JsonNode? node) =>
        node is JsonObject definition && definition["order"] is JsonValue value
            ? value.TryGetValue<int>(out var order) ? order
            : value.GetValueKind() == JsonValueKind.Number && int.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out order) ? order
            : null
            : null;

    /// <summary>A distinct definition of a node: the combinations that define it so, compiled once, at its order.</summary>
    private sealed record NodeDefinition(NodeVariants Variants, long Order, List<int> Combinations);

    /// <summary>Groups a node's combinations by definition (equal content, compiled once).</summary>
    private static void AddDistinctDefinitions(NodeVariants variants, List<NodeDefinition> timeline)
    {
        var byHash = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        for (var c = 0; c < variants.Definitions.Length; c++)
        {
            if (variants.Definitions[c] is not { } node || variants.Failed[c])
            {
                continue;
            }

            var hash = ContentHash.Compute(node);
            if (!byHash.TryGetValue(hash, out var definition))
            {
                var order = OrderOf(node) ?? long.MaxValue;
                definition = new NodeDefinition(variants, order, new List<int>());
                byHash[hash] = definition;
                timeline.Add(definition);
            }

            definition.Combinations.Add(c);
        }
    }

    /// <summary>Each combination's definition of a node (<see cref="NodeVariants.Definitions"/>), compiled afterwards.</summary>
    private NodeVariants? DefineNodeVariants(string nodeId)
    {
        var pointer = "/nodes/" + JsonPointer.Escape(nodeId);
        if (BuildSpace(arm => arm.TouchedNodes.Contains(nodeId), $"Node '{nodeId}'", pointer) is not { } space)
        {
            return null;
        }

        var variants = new NodeVariants
        {
            NodeId = nodeId,
            Layers = space.Layers,
            LocalArms = space.LocalArms,
            LocalIndex = space.LocalIndex,
            Radix = space.Radix,
            Definitions = new JsonObject?[space.Count],
            Variants = new CompiledNode?[space.Count],
            Failed = new bool[space.Count],
        };
        _variants[nodeId] = variants;

        var baseline = _view.Nodes[nodeId]?.DeepClone();
        bool? fallbackRole = null;
        for (var c = 0; c < space.Count; c++)
        {
            // A combination with an arm that yields never runs as such: it is defined as it runs, without that arm.
            var armsInCombination = Running(ArmPatches(variants.ArmsOf(c)));
            var definition = Merge(baseline, armsInCombination, "nodes", nodeId);
            if (definition is null)
            {
                continue; // the node does not exist in this combination
            }

            var start = _diagnostics.Count;
            if (definition is not JsonObject node)
            {
                _diagnostics.Error("NODE_INVALID", _pipelineLabel, pointer, "A node must be an object.");
                variants.Failed[c] = true;
                Settle(start, armsInCombination);
                continue;
            }

            variants.Definitions[c] = node;
            var isFallback = node["role"] is JsonValue role && role.TryGetValue<string>(out var r) && r == "fallback";
            if (fallbackRole is { } expected && expected != isFallback)
            {
                _diagnostics.Error("NODE_ROLE_CHANGED", _pipelineLabel, pointer + "/role",
                    $"Experiments cannot turn '{nodeId}' between a timeline node and a fallback.", "add a new node id instead");
                variants.Failed[c] = true;
                Settle(start, armsInCombination);
                continue;
            }

            fallbackRole = isFallback;
        }

        variants.IsFallback = fallbackRole == true;
        return variants;
    }

    /// <summary>Compiles every tap variant. Runs after the nodes: <c>columns_from</c> needs them.</summary>
    private void CompileTapVariants()
    {
        var taps = (JsonObject)_view.Document["log_taps"]!;
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in taps)
        {
            ids.Add(id);
        }

        foreach (var arm in _arms)
        {
            if (arm.Valid)
            {
                ids.UnionWith(arm.TouchedTaps);
            }
        }

        foreach (var tapId in ids)
        {
            var pointer = "/log_taps/" + JsonPointer.Escape(tapId);

            // columns_from logs a model node's inputs: the tap must vary with every layer that changes that node, or a
            // treatment would serve one input contract while its training log records another.
            var models = ColumnsFromNodes(tapId, taps[tapId] as JsonObject);
            if (BuildSpace(arm => arm.TouchedTaps.Contains(tapId) || TouchesAny(arm, models), $"Tap '{tapId}'", pointer) is not { } space)
            {
                continue;
            }

            var variants = new TapVariants
            {
                TapId = tapId,
                Layers = space.Layers,
                LocalArms = space.LocalArms,
                LocalIndex = space.LocalIndex,
                Radix = space.Radix,
                Variants = new CompiledTap?[space.Count],
                Failed = new bool[space.Count],
            };
            _taps[tapId] = variants;
            var baseline = taps[tapId]?.DeepClone();
            var byHash = new Dictionary<string, (CompiledTap? Tap, bool Failed)>(StringComparer.Ordinal);
            for (var c = 0; c < space.Count; c++)
            {
                var armsInCombination = Running(ArmPatches(variants.ArmsOf(c)));
                var definition = Merge(baseline, armsInCombination, "log_taps", tapId);
                if (definition is null)
                {
                    continue;
                }

                var start = _diagnostics.Count;
                if (definition is not JsonObject tap)
                {
                    _diagnostics.Error("TAP_INVALID", _pipelineLabel, pointer, "A tap must be an object.");
                    variants.Failed[c] = true;
                    Settle(start, armsInCombination);
                    continue;
                }

                var key = ContentHash.Compute(tap);
                foreach (var model in models)
                {
                    key += "|" + model + ":" + (_variants.TryGetValue(model, out var mv) ? mv.IndexOfArms(armsInCombination) : -1);
                }

                if (!byHash.TryGetValue(key, out var compiled))
                {
                    var result = _builder.CompileTap(tapId, tap, _pipelineLabel, nodeId => ResolveVariant(nodeId, armsInCombination));
                    // A node no combination has, with a suggestion for the typo. One that only some combinations have is
                    // judged in each (PlanBuilder.ValidateDataFlow), as a compile of that combination alone judges it.
                    if (result?.AttachAfter is { } after && !_variants.ContainsKey(after))
                    {
                        _diagnostics.Error("TAP_UNKNOWN_NODE", _pipelineLabel, pointer + "/attach/after", $"Tap '{tapId}' attaches after unknown node '{after}'.",
                            NameSuggestions.DidYouMean(after, _variants.Keys) ?? "attach after a node id from /nodes, or use {\"output\": true}");
                        result = null;
                    }

                    compiled = (result, result is null);
                    byHash[key] = compiled;
                }

                variants.Variants[c] = compiled.Tap;
                variants.Failed[c] = compiled.Failed;
                Settle(start, armsInCombination);
            }
        }
    }

    /// <summary>Model nodes named by <c>columns_from</c> in the baseline tap or in any arm's patch of it.</summary>
    private SortedSet<string> ColumnsFromNodes(string tapId, JsonObject? baseline)
    {
        var nodes = new SortedSet<string>(StringComparer.Ordinal);
        Add(baseline);
        foreach (var arm in _arms)
        {
            if (arm.Valid && arm.TouchedTaps.Contains(tapId) && arm.Patch["log_taps"] is JsonObject patched)
            {
                Add(patched[tapId] as JsonObject);
            }
        }

        return nodes;

        void Add(JsonObject? tap)
        {
            if (tap?["columns_from"] is JsonValue value && value.TryGetValue<string>(out var from) && from.EndsWith(".inputs", StringComparison.Ordinal))
            {
                nodes.Add(from[..^7]);
            }
        }
    }

    private static bool TouchesAny(ArmPatch arm, SortedSet<string> nodes)
    {
        foreach (var node in nodes)
        {
            if (arm.TouchedNodes.Contains(node))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The variant of <paramref name="nodeId"/> in effect for an arm combination, or null when absent there.</summary>
    private CompiledNode? ResolveVariant(string nodeId, IReadOnlyList<ArmPatch> arms) =>
        _variants.TryGetValue(nodeId, out var variants) ? variants.Variants[variants.IndexOfArms(arms)] : null;

    private LogPlan BuildLogPlan()
    {
        var slots = new List<TapSlot>();
        foreach (var (tapId, v) in _taps)
        {
            slots.Add(new TapSlot
            {
                TapId = tapId,
                TouchingLayers = v.Layers,
                LocalIndex = v.LocalIndex,
                Radix = v.Radix,
                Variants = ImmutableArray.Create(v.Variants),
            });
        }

        var groups = new CompiledFunnelGroup[_builder.FunnelGroups.Count];
        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = _builder.FunnelGroups[i];
        }

        return new LogPlan { Groups = ImmutableCollectionsMarshal.AsImmutableArray(groups), Taps = [.. slots] };
    }

    private (ImmutableArray<TimelineSlot> Timeline, Dictionary<string, TimelineSlot> Fallbacks) BuildSlots()
    {
        var timeline = new List<TimelineSlot>();
        var fallbacks = new Dictionary<string, TimelineSlot>(StringComparer.Ordinal);
        foreach (var (nodeId, v) in _variants)
        {
            if (v.IsFallback)
            {
                fallbacks[nodeId] = NewSlot(v, 0, ImmutableArray.Create(v.Variants));
                continue;
            }

            var orders = new SortedSet<int>();
            foreach (var node in v.Variants)
            {
                if (node?.Order is { } order)
                {
                    orders.Add(order);
                }
            }

            // A moved node occupies one position per order, with complementary variant arrays.
            foreach (var order in orders)
            {
                var selected = new CompiledNode?[v.Variants.Length];
                for (var i = 0; i < selected.Length; i++)
                {
                    selected[i] = v.Variants[i]?.Order == order ? v.Variants[i] : null;
                }

                timeline.Add(NewSlot(v, order, ImmutableCollectionsMarshal.AsImmutableArray(selected)));
            }
        }

        timeline.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.NodeId, b.NodeId));
        return ([.. timeline], fallbacks);
    }

    private static TimelineSlot NewSlot(NodeVariants v, int order, ImmutableArray<CompiledNode?> variants) => new()
    {
        Order = order,
        NodeId = v.NodeId,
        TouchingLayers = v.Layers,
        LocalIndex = v.LocalIndex,
        Radix = v.Radix,
        Variants = variants,
    };

    // ------------------------------------------------------------------ trigger sites

    private void AssignTriggerSites(
        ImmutableArray<TimelineSlot> timeline, Dictionary<string, TimelineSlot> fallbacks, OutputPlan? output, Dictionary<int, SortedSet<int>> leafExperiments)
    {
        foreach (var slot in timeline)
        {
            AssignSite(slot);
        }

        foreach (var (_, slot) in fallbacks)
        {
            AssignSite(slot);
        }

        // Parameter sites: a node variant (or the output) that reads a leaf some arm of E overrides.
        foreach (var node in _builder.Nodes)
        {
            node.ParamExperiments = ExperimentsReading(node.ReadParams, leafExperiments);
        }

        if (output is not null)
        {
            var reads = new List<int> { output.Limit.Id };
            foreach (var expression in output.Expressions())
            {
                foreach (var symbol in expression.Reads)
                {
                    if (symbol.Kind == SymbolKind.Param)
                    {
                        reads.Add(symbol.Id);
                    }
                }
            }

            output.ParamExperiments = ExperimentsReading(reads, leafExperiments);
        }
    }

    private static ImmutableArray<int> ExperimentsReading(IReadOnlyList<int> leaves, Dictionary<int, SortedSet<int>> leafExperiments)
    {
        SortedSet<int>? result = null;
        foreach (var leaf in leaves)
        {
            if (leafExperiments.TryGetValue(leaf, out var set))
            {
                (result ??= new SortedSet<int>()).UnionWith(set);
            }
        }

        return result is null ? [] : ImmutableCollectionsMarshal.AsImmutableArray(ToArray(result));
    }

    /// <summary>
    /// The experiments whose site includes a position: those with an arm that modifies, adds, removes or
    /// moves the node here. Whether reaching it triggers one is decided per request from its arms' nodes
    /// (<see cref="TimelineSlot.SelectWith"/>), since the other layers' arms of the request decide which node each arm has.
    /// </summary>
    private void AssignSite(TimelineSlot slot)
    {
        var experiments = new SortedSet<int>();
        foreach (var arms in _variants[slot.NodeId].LocalArms)
        {
            foreach (var arm in arms)
            {
                experiments.Add(_arms[arm].ExperimentIndex);
            }
        }

        slot.SiteExperiments = [.. experiments];
    }

    // ------------------------------------------------------------------ helpers

    private RequestArms OnlyArm(int armIndex)
    {
        var byLayer = new int[_layers.Count];
        Array.Fill(byLayer, -1);
        byLayer[_arms[armIndex].LayerIndex] = armIndex;
        return new RequestArms(byLayer);
    }

    /// <summary>
    /// A combination (an arm per layer) as it runs: without the arms whose experiment yields to another experiment of it
    /// (<see cref="ArmYields"/>), the same array when none does.
    /// </summary>
    private int[] Running(int[] combination)
    {
        if (_yields.IsEmpty)
        {
            return combination;
        }

        var yielded = new List<int>();
        _yields.Resolve(new RequestArms(combination), yielded);
        if (yielded.Count == 0)
        {
            return combination;
        }

        var running = (int[])combination.Clone();
        foreach (var arm in yielded)
        {
            running[_arms[arm].LayerIndex] = -1;
        }

        return running;
    }

    /// <inheritdoc cref="Running(int[])"/>
    private List<ArmPatch> Running(List<ArmPatch> arms)
    {
        if (_yields.IsEmpty || arms.Count < 2)
        {
            return arms;
        }

        var combination = NewCombination();
        foreach (var arm in arms)
        {
            combination[arm.LayerIndex] = arm.Index;
        }

        var yielded = new List<int>();
        _yields.Resolve(new RequestArms(combination), yielded);
        if (yielded.Count == 0)
        {
            return arms;
        }

        var running = new List<ArmPatch>(arms.Count);
        foreach (var arm in arms)
        {
            if (!yielded.Contains(arm.Index))
            {
                running.Add(arm);
            }
        }

        return running;
    }

    private List<ArmPatch> ArmPatches(List<int> indices)
    {
        var list = new List<ArmPatch>(indices.Count);
        foreach (var i in indices)
        {
            list.Add(_arms[i]);
        }

        return list;
    }

    /// <summary>
    /// Moves diagnostics recorded since <paramref name="start"/> back into the bag, deduplicated, and attributes each
    /// to the arm whose patch touches its location so the message says where to fix it.
    /// </summary>
    private void Settle(int start, IReadOnlyList<ArmPatch> arms)
    {
        if (_diagnostics.Count == start)
        {
            return;
        }

        foreach (var d in _diagnostics.RemoveFrom(start))
        {
            Report(d, arms);
        }
    }

    /// <summary>
    /// Reports a diagnostic once per root cause. The dedupe key is (owning arm, code, view pointer, message), so the
    /// same baseline problem seen again under arm combinations, or an arm's problem found by both the unified and the
    /// per-arm reference compile, is reported once.
    /// </summary>
    private void Report(ConfigDiagnostic d, IReadOnlyList<ArmPatch> arms)
    {
        var owner = string.Empty;
        var viewPointer = d.Pointer;
        var final = d;
        if (d.Artifact.StartsWith("experiment:", StringComparison.Ordinal))
        {
            owner = d.Artifact;
            foreach (var arm in _arms)
            {
                if (d.Artifact == arm.Artifact && (d.Pointer == arm.PointerPrefix || d.Pointer.StartsWith(arm.PointerPrefix + "/", StringComparison.Ordinal)))
                {
                    owner = arm.Id;
                    viewPointer = d.Pointer[arm.PointerPrefix.Length..];
                    break;
                }
            }
        }
        else if (arms.Count > 0)
        {
            ArmPatch? match = null;
            var matches = 0;
            foreach (var arm in arms)
            {
                if (arm.Relates(d.Pointer))
                {
                    match = arm;
                    matches++;
                }
            }

            var ids = new List<string>();
            foreach (var arm in arms)
            {
                if (matches != 1 || arm != match)
                {
                    ids.Add(arm.Id);
                }
            }

            if (matches == 1)
            {
                owner = match!.Id;
                var together = ids.Count == 0 ? string.Empty : $" (together with {string.Join(", ", ids)})";
                final = d with
                {
                    Artifact = match.Artifact, Pointer = match.PointerPrefix + d.Pointer, Message = d.Message + together,
                    Brief = d.Brief is null ? null : d.Brief + together,
                };
            }
            else
            {
                final = d with { Artifact = $"{d.Artifact} with {string.Join(" + ", ids)}" };
            }
        }

        if (_reported.Add(owner + "\n" + d.Code + "\n" + viewPointer + "\n" + d.Message))
        {
            _diagnostics.Add(final);
        }
    }

    /// <summary>Attributes schema diagnostics about add-only additions (e.g. a bad column type) to the arm that added them.</summary>
    private void SettleAdditions(int start)
    {
        if (_diagnostics.Count == start)
        {
            return;
        }

        foreach (var d in _diagnostics.RemoveFrom(start))
        {
            ArmPatch? arm = null;
            if (JsonPointer.TryParse(d.Pointer, out var pointer) && pointer.Depth >= 2
                && _additions.TryGetValue(pointer.Tokens[0], out var entries) && entries.TryGetValue(pointer.Tokens[1], out var added))
            {
                arm = added.Arm;
            }

            Report(arm is null ? d : d with { Artifact = arm.Artifact, Pointer = arm.PointerPrefix + d.Pointer }, Array.Empty<ArmPatch>());
        }
    }

    private ImmutableArray<LayerInfo> LayerInfos()
    {
        var layers = new LayerInfo[_layers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            layers[i] = new LayerInfo(i, _layers[i]);
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(layers);
    }

    private ImmutableArray<ExperimentInfo> ExperimentInfos()
    {
        var experiments = new ExperimentInfo[_experiments.Count];
        for (var i = 0; i < experiments.Length; i++)
        {
            var (id, layer, arms) = _experiments[i];
            experiments[i] = new ExperimentInfo(i, id, layer, arms) { YieldsTo = _yields.YieldsOf(i) };
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(experiments);
    }

    private ImmutableArray<ArmInfo> ArmInfos()
    {
        var arms = new ArmInfo[_arms.Count];
        foreach (var arm in _arms)
        {
            arms[arm.Index] = new ArmInfo(arm.Index, arm.Id, arm.ArmName, arm.ExperimentIndex, arm.LayerIndex);
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(arms);
    }

    private List<NodeVariantReport> NodeReports()
    {
        var reports = new List<NodeVariantReport>();
        foreach (var (nodeId, v) in _variants)
        {
            var layers = new List<string>();
            foreach (var layer in v.Layers)
            {
                layers.Add(_layers[layer]);
            }

            var distinct = new HashSet<CompiledNode>(ReferenceEqualityComparer.Instance);
            var orders = new SortedSet<int>();
            foreach (var node in v.Variants)
            {
                if (node is not null)
                {
                    distinct.Add(node);
                    if (node.Order is { } order)
                    {
                        orders.Add(order);
                    }
                }
            }

            reports.Add(new NodeVariantReport(nodeId, layers, v.Variants.Length, distinct.Count, ToArray(orders)));
        }

        return reports;
    }

    private List<ArmReport> ArmReports()
    {
        var reports = new List<ArmReport>();
        foreach (var arm in _arms)
        {
            var paths = new List<string>();
            foreach (var op in arm.Footprint)
            {
                paths.Add(op.ToString());
            }

            var leaves = new List<string>();
            foreach (var ov in arm.ParamOverrides)
            {
                leaves.Add(ov.Leaf);
            }

            var owners = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (prefix, teams) in _request.Policy.Owners)
            {
                if (arm.Relates(prefix))
                {
                    owners.UnionWith(teams);
                }
            }

            reports.Add(new ArmReport(arm.Id, _layers[arm.LayerIndex], paths, leaves, new List<string>(arm.TouchedNodes), new List<string>(owners)));
        }

        return reports;
    }

    private static int[] ToArray(SortedSet<int> set)
    {
        var array = new int[set.Count];
        set.CopyTo(array);
        return array;
    }
}
