using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Experiments.Compilation;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The unified plan against the plain compile of each arm combination's merged config (design 16.2), on generated
/// experiments: the unified plan accepts exactly what every combination accepts (it may refuse more only by a rule across
/// combinations, such as one type per column), and serves each combination as its merged compile does. The fixed
/// sample's eight combinations did not find that a baseline could read a column only an arm's node declares (removing the
/// experiment broke it), or that an arm moving a reader after a writer was refused (external review); generated arms
/// that add, move, delete and change nodes reading implicit columns do.
/// </summary>
public sealed class GeneratedExperimentTests
{
    /// <summary>Rules of the unified plan across combinations: they may refuse a config every combination accepts.</summary>
    private static readonly HashSet<string> AcrossCombinations = new(StringComparer.Ordinal)
    {
        "COLUMN_TYPE_MISMATCH", "PATCH_CONFLICT", "ADD_CONFLICT", "ORDER_COLLISION", "NODE_ROLE_CHANGED",
        "COMBINATION_CHECK_LIMIT", // a rule over more combinations than may be checked is refused, not left unchecked
    };

    /// <summary>300 configs (about 25 s); set <c>STRATAREC_GENERATED_SEEDS</c> to run more, e.g. 3000 after changing the compiler.</summary>
    public static TheoryData<int> Seeds =>
        new(Enumerable.Range(0, int.TryParse(Environment.GetEnvironmentVariable("STRATAREC_GENERATED_SEEDS"), out var seeds) ? seeds : 300));

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task TheUnifiedPlan_AcceptsWhatEveryCombinationAccepts_AndServesEachAsItsMergedCompile(int seed)
    {
        var host = HomeFeedTestHost.Load();
        var (request, layers, description) = Generate(seed, host.Request, host.Environment);
        var combinations = Combinations(layers);

        var unified = PlanCompiler.Compile(request, host.Environment);
        var refused = new List<string>();
        foreach (var arms in combinations)
        {
            var merged = PlanCompiler.CompileMerged(request, arms, host.Environment);
            if (!merged.Succeeded)
            {
                refused.Add($"[{string.Join(", ", arms)}]: {string.Join("; ", merged.Diagnostics.Where(d => d.IsError))}");
            }
        }

        if (unified.Succeeded)
        {
            Assert.True(refused.Count == 0, $"The unified plan accepted what a combination refuses:\n{string.Join("\n", refused)}\n{description}");
        }
        else if (refused.Count == 0)
        {
            // A column takes the first writer's type in the unified schema, so a reader may also be refused for what that
            // type implies (a nullable read without a default): declaring the column as the hint says has the same effect.
            var errors = unified.Diagnostics.Where(d => d.IsError).ToList();
            var mismatch = errors.Any(d => d.Code == "COLUMN_TYPE_MISMATCH");
            Assert.True(errors.All(d => AcrossCombinations.Contains(d.Code) || (mismatch && d.Code == "EXPR_NULLABLE_RESULT")),
                $"The unified plan refused what every combination accepts:\n{string.Join("\n", errors)}\n{description}");
        }

        if (unified.Succeeded)
        {
            await ServesEachCombinationAsItsMergedCompile(host, request, combinations, description);
        }
    }

    private static async Task ServesEachCombinationAsItsMergedCompile(
        HomeFeedTestHost host, PlanCompileRequest request, List<string[]> combinations, string description)
    {
        var unified = Loaded(host, bindings => PlanCompiler.Compile(request, host.Environment, bindings));
        var executor = new PipelineExecutor();
        foreach (var arms in combinations)
        {
            var reference = Loaded(host, bindings => PlanCompiler.CompileMerged(request, arms, host.Environment, bindings));
            var actual = await executor.ExecuteAsync(unified, HomeFeedTestHost.NewRequest(arms: unified.ArmsFromIds(arms)), TestContext.Current.CancellationToken);
            var expected = await executor.ExecuteAsync(reference, HomeFeedTestHost.NewRequest(), TestContext.Current.CancellationToken);
            Assert.True(actual.Succeeded == expected.Succeeded, $"[{string.Join(", ", arms)}]: {actual.Error} / {expected.Error}\n{description}");
            LiveRunAssert.SameNodeOutcomes(expected, actual);
            Assert.True(Describe(expected) == Describe(actual), $"[{string.Join(", ", arms)}] served differently\n{description}");
        }
    }

    /// <summary>Compiles with the local tables loaded, as the engine does before serving a plan.</summary>
    private static ExecutionPlan Loaded(HomeFeedTestHost host, Func<IPlanBindings, PlanCompilation> compile)
    {
        var lease = host.Bindings.OpenLease();
        var compilation = lease.CompileAsync(compile).GetAwaiter().GetResult();
        Assert.Empty(lease.WaitLoadedAsync().GetAwaiter().GetResult());
        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
        return compilation.Plan!;
    }

    private static string Describe(PipelineResult result) =>
        string.Join(";", result.Items.Select(i => $"{i.Rank}:{i.Id}:{string.Join(",", i.Columns.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}"));

    /// <summary>Each layer: no arm, or one of its arms.</summary>
    private static List<string[]> Combinations(List<List<string>> layers)
    {
        var combinations = new List<string[]> { Array.Empty<string>() };
        foreach (var arms in layers)
        {
            combinations = combinations.SelectMany(c => arms.Select(a => c.Append(a).ToArray()).Prepend(c)).ToList();
        }

        return combinations;
    }

    /// <summary>
    /// The sample with generated experiments, and sometimes a baseline node reading a column only an arm may declare.
    /// Implicit columns are <c>stat.a</c> and <c>stat.b</c>, written by GroupAggregate@1 (nullable when grouped by a key
    /// that may be missing), read with or without <c>??</c>, at random places on the timeline.
    /// </summary>
    private static (PlanCompileRequest Request, List<List<string>> Layers, string Description) Generate(int seed, PlanCompileRequest sample, PlanEnvironment environment)
    {
        var random = new Random(seed);
        var pipeline = sample.Pipeline;
        var described = new List<string>();
        if (random.Next(5) == 0)
        {
            var reader = Reader(random, random.Next(2) == 0 ? "col." + Stat(random) : null);
            pipeline.Nodes["base_reader"] = new NodeSpec { Order = ReaderOrder(random) + 1, Op = "ExpressionFilter@1", Config = new JsonObject { ["predicate"] = reader } };
            described.Add($"baseline node base_reader: {reader}");
        }

        // With four or five layers of mostly one treatment, the space is past a limit of 8 to 16 that a scope of three
        // layers fits, so a scope is checked by its combinations, not refused (review 51: no generated space checked one).
        // Half of those plant a rule only three layers break together: a baseline writer of stat.a before author_cap,
        // one arm making author_cap read it, one moving author_cap to 513 and one moving the writer to 514. Any one or two
        // of them are legal (review 51: a scope check left out went unnoticed, nothing random was refused by three layers).
        var many = random.Next(4) == 0;
        var triple = many && random.Next(2) == 0;
        var baseWriter = triple || random.Next(3) == 0;
        if (baseWriter)
        {
            var writer = Producer(random, "stat.a");
            if (triple)
            {
                writer["order"] = Orders[random.Next(4)]; // before 513
            }

            pipeline.Nodes["base_writer"] = new NodeSpec
            {
                Order = (int)writer["order"]!, Op = "GroupAggregate@1", Config = writer["config"]!.DeepClone().AsObject(),
            };
            described.Add($"baseline node base_writer: {writer.ToJsonString()}");
        }

        // Sometimes without each arm's own compile (a host with a thousand arms turns it off), so the combinations' checks
        // alone must refuse what a combination refuses, and then with more arms invalid on their own; sometimes past the
        // combinations checked in full, so the checks by scope must (review 50: the generated space never left the
        // exhaustive path).
        var alone = random.Next(3) != 0;
        var limit = many ? 8 + (4 * random.Next(3)) : random.Next(3) == 0 ? random.Next(1, 8) : sample.MaxCombinationsChecked;
        described.Add($"arms validated alone: {alone}; combinations checked in full: up to {limit}; three layers planted: {triple}");
        var placements = new List<ExperimentPlacement>();
        var layers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var experiments = many ? random.Next(4, 6) : random.Next(1, 4);
        for (var e = 0; e < experiments; e++)
        {
            var id = "g" + e;
            // Mostly with a control arm, which is the baseline: without one, only the baseline's own check (a request no
            // experiment assigns, or the experiment's end) refuses a baseline that needs an arm (external review P1).
            var arms = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var armIds = new List<string>();
            if (random.Next(4) != 0)
            {
                arms["control"] = new JsonObject();
                armIds.Add(id + "/control");
            }

            var treatments = many && random.Next(4) != 0 ? 1 : random.Next(1, 3);
            for (var t = 0; t < treatments; t++)
            {
                // Mostly an arm valid on its own: one invalid arm refuses the unified plan, and few would be accepted.
                var keepInvalid = random.Next(alone ? 4 : 2) == 0;
                JsonObject patch;
                var tries = 0;
                if (triple && e < 3 && t == 0)
                {
                    patch = e switch
                    {
                        0 => Patch("nodes", "author_cap", new JsonObject { ["config"] = new JsonObject { ["predicate"] = "(col.stat.a ?? 1) > 0" } }),
                        1 => Patch("nodes", "author_cap", new JsonObject { ["order"] = 513 }),
                        _ => Patch("nodes", "base_writer", new JsonObject { ["order"] = 514 }),
                    };
                }
                else
                {
                    do
                    {
                        patch = new JsonObject();
                        var atoms = random.Next(1, 3);
                        for (var a = 0; a < atoms; a++)
                        {
                            Merge(patch, Atom(random, $"{id}_{t}_{a}", baseWriter));
                        }
                    }
                    while (!keepInvalid && ++tries < 10 && !ValidAlone(sample, pipeline, patch, environment));
                }

                arms["t" + t] = patch;
                armIds.Add($"{id}/t{t}");
                described.Add($"{id}/t{t}: {patch.ToJsonString()}");
            }

            // Mostly a layer of its own; sometimes the previous experiment's (mutually exclusive with it).
            var layer = e > (triple ? 2 : 0) && random.Next(many ? 8 : 4) == 0 ? placements[^1].Layer : "layer_" + id;
            placements.Add(new ExperimentPlacement(Experiment(id, arms), layer));
            if (!layers.TryGetValue(layer, out var layerArms))
            {
                layers[layer] = layerArms = [];
            }

            layerArms.AddRange(armIds);
        }

        var request = new PlanCompileRequest
        {
            Pipeline = pipeline, Catalog = sample.Catalog, Bindings = sample.Bindings, Tables = sample.Tables, Policy = sample.Policy,
            Experiments = placements, ManifestId = sample.ManifestId, ValidateArmsIndependently = alone, MaxCombinationsChecked = limit,
        };
        return (request, layers.Values.ToList(), $"seed {seed}:\n  " + string.Join("\n  ", described));
    }

    private static bool ValidAlone(PlanCompileRequest sample, PipelineArtifact pipeline, JsonObject patch, PlanEnvironment environment)
    {
        var probe = new PlanCompileRequest
        {
            Pipeline = pipeline, Catalog = sample.Catalog, Bindings = sample.Bindings, Tables = sample.Tables, Policy = sample.Policy,
            Experiments = [new ExperimentPlacement(Experiment("probe", new() { ["t"] = patch }), "probe")],
            ManifestId = sample.ManifestId,
        };
        return PlanCompiler.CompileMerged(probe, ["probe/t"], environment).Succeeded;
    }

    /// <summary>
    /// The experiment as a host builds it in code: its patches hold CLR numbers (an order is an <c>int</c>), which a trip
    /// through JSON text would turn into parsed elements (review 49: only a parsed order was read).
    /// </summary>
    private static ExperimentArtifact Experiment(string id, Dictionary<string, JsonObject> arms) => new()
    {
        ExperimentId = id, PipelineId = "home_feed", Owner = "t", Description = "d", ExpiresAt = "2099-01-01", Arms = arms,
    };

    /// <summary>One change an arm makes; <paramref name="baseWriter"/> when the baseline has a writer of stat.a.</summary>
    private static JsonObject Atom(Random random, string suffix, bool baseWriter)
    {
        switch (random.Next(16))
        {
            case 0 or 1:
                return random.Next(2) == 0
                    ? Patch("params", "quality_weight", JsonValue.Create(0.9))
                    : Patch("params", "coarse_k", JsonValue.Create(20));
            case 2:
                return Patch("nodes", "agg_" + suffix, Producer(random, Stat(random)));
            case 3 or 4:
            {
                // A writer and a reader of its column, in either order: the arm is refused when the reader comes first.
                var column = Stat(random);
                return new JsonObject
                {
                    ["nodes"] = new JsonObject
                    {
                        ["agg_" + suffix] = Producer(random, column),
                        ["read_" + suffix] = Filter(Order(random), Reader(random, "col." + column)),
                    },
                };
            }
            case 5 or 6:
            {
                // A baseline node moved after a writer the arm adds, reading what it writes (external review P2).
                var column = Stat(random);
                var producer = Producer(random, column);
                var after = random.Next(4) == 0 ? Order(random) : (int)producer["order"]! + 1 + random.Next(20);
                return new JsonObject
                {
                    ["nodes"] = new JsonObject
                    {
                        ["agg_" + suffix] = producer,
                        ["author_cap"] = new JsonObject { ["order"] = after, ["config"] = new JsonObject { ["predicate"] = Reader(random, "col." + column) } },
                    },
                };
            }
            case 7:
                return Patch("nodes", "read_" + suffix, Filter(ReaderOrder(random), Reader(random)));
            case 8:
                return Patch("nodes", "author_cap", new JsonObject
                {
                    ["config"] = new JsonObject { ["predicate"] = Reader(random, baseWriter && random.Next(2) == 0 ? "col.stat.a" : null) },
                });
            case 9:
                return Patch("nodes", (baseWriter ? random.Next(3) : random.Next(2)) switch { 0 => "author_cap", 1 => "diversity_cap", _ => "base_writer" },
                    new JsonObject { ["order"] = Order(random) });
            case 10:
                return random.Next(3) == 0 ? Patch("nodes", "author_rank", null) : Patch("nodes", random.Next(2) == 0 ? "author_cap" : "diversity_cap", null);
            case 11 or 12:
                return Patch("nodes", "boost_" + suffix, new JsonObject
                {
                    ["order"] = ReaderOrder(random), ["op"] = "ExpressionScorer@1",
                    ["config"] = new JsonObject { ["output_col"] = "score.final", ["expr"] = $"({Reader(random)}) ? col.score.final * 1.5 : col.score.final" },
                });
            case 14 when random.Next(2) == 0:
                // The sample's author rank ranking by itself: it reads its value column before writing its output, so in
                // a compile of this arm alone the name is unknown, though the baseline's variant declares it (review 50).
                return Patch("nodes", "author_rank", new JsonObject
                {
                    ["config"] = new JsonObject { ["outputs"] = new JsonObject { ["group.author_rank"] = new JsonObject { ["value_col"] = "group.author_rank" } } },
                });
            case 14:
            {
                // One node writing a count and ranking rows by it: GroupAggregate@1 compiles its outputs by name, so self.b
                // may read the self.a it has just written, but self.a reads self.b before writing it, which only a node
                // compiled earlier can have declared (review 50). Columns of their own, int64 either way, so a type
                // mismatch with other writers does not refuse the plan before the declarations are checked.
                var (first, second) = random.Next(2) == 0 ? ("self.a", "self.b") : ("self.b", "self.a");
                return Patch("nodes", "self_" + suffix, new JsonObject
                {
                    ["order"] = Order(random), ["op"] = "GroupAggregate@1",
                    ["config"] = new JsonObject
                    {
                        ["outputs"] = new JsonObject
                        {
                            [first] = new JsonObject { ["fn"] = "count" },
                            [second] = new JsonObject { ["fn"] = "rank", ["value_col"] = first },
                        },
                    },
                });
            }
            case 15:
                // A declaration of a column nodes may also write without one: it exists only where the arm is assigned,
                // so a reader in another arm cannot rely on it there (review 51 P1).
                return Patch("columns", random.Next(3) == 0 ? "group.author_rank" : Stat(random), new JsonObject { ["type"] = "int64?" });
            default:
                return Patch("log_taps", "tap_" + suffix, new JsonObject
                {
                    ["group"] = "all", ["attach"] = new JsonObject { ["after"] = random.Next(2) == 0 ? "author_rank" : "rank_sort" },
                    ["columns"] = new JsonObject { ["v"] = random.Next(2) == 0 ? "col.score.final" : "col.group.author_rank" },
                });
        }
    }

    private static string Stat(Random random) => random.Next(2) == 0 ? "stat.a" : "stat.b";

    private static JsonObject Filter(int order, string predicate) => new()
    {
        ["order"] = order, ["op"] = "ExpressionFilter@1", ["config"] = new JsonObject { ["predicate"] = predicate },
    };

    /// <summary>A GroupAggregate@1 writing <paramref name="column"/>, nullable when grouped by the author (who may be missing).</summary>
    private static JsonObject Producer(Random random, string column)
    {
        var config = new JsonObject { ["outputs"] = new JsonObject { [column] = new JsonObject { ["fn"] = "count" } } };
        if (random.Next(2) == 0)
        {
            config["group_col"] = "item.author_id";
        }

        return new JsonObject { ["order"] = Order(random), ["op"] = "GroupAggregate@1", ["config"] = config };
    }

    /// <summary>
    /// A predicate reading <paramref name="column"/>, or mostly the sample's author rank (a stat column only an arm
    /// writes), mostly with a default; sometimes in a branch the optimizer drops, where the name must still be declared
    /// (review 51).
    /// </summary>
    private static string Reader(Random random, string? column = null)
    {
        column ??= random.Next(4) == 0 ? "col." + Stat(random) : "col.group.author_rank";
        var read = random.Next(6) == 0 ? column + " > 0" : $"({column} ?? 1) > 0";
        return random.Next(8) switch
        {
            0 => $"true || ({read})",
            1 => $"false ? ({read}) : true",
            _ => read,
        };
    }

    /// <summary>Around the sample's fusion, sort, author rank, author cap and diversity cap (450, 500, 512, 515, 520).</summary>
    private static int Order(Random random) => Orders[random.Next(Orders.Length)];

    /// <summary>Mostly after the author rank (512), which readers that do not name a column read.</summary>
    private static int ReaderOrder(Random random) => random.Next(4) == 0 ? Order(random) : Orders[4 + random.Next(Orders.Length - 4)];

    private static readonly int[] Orders = [460, 505, 508, 510, 513, 514, 516, 517, 525, 530, 540];

    private static JsonObject Patch(string section, string id, JsonNode? value) => new() { [section] = new JsonObject { [id] = value } };

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (target[key] is JsonObject existing && value is JsonObject nested)
            {
                Merge(existing, nested);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
