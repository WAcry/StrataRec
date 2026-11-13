using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Models;
using StrataRec.Pipeline.Replay;
using StrataRec.UnitTests.Features;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// How a replay answers calls from a recorded run (design 13.4): exact calls first, then values synthesized from the
/// successful calls, with whatever the recording lacks reported as a gap.
/// </summary>
public sealed class RunReplayTests
{
    [Fact]
    public void ARecordedValueBeyondFloat32_IsATypeChangedGap_AndMissing_InExactAndSynthesizedCalls()
    {
        // Recorded as float64; the replayed catalog declares the field float32. 1e100 does not fit (it would become +∞),
        // a recorded +∞ is +∞ either way, and 0.5 rounds like any float32.
        var run = Run();
        run.FeatureCalls.Add(Feature(0, "[1, 2, 3]", ("score", "float64", "[1e100, 0.5, \"Infinity\"]")));
        var replay = RunReplay.Create(run, "/runs/0");

        var (exactStatus, exact) = Fetch(replay, new long[] { 1, 2, 3 }, ("score", "float32")); // the recorded call
        var (synthesizedStatus, synthesized) = Fetch(replay, new long[] { 3, 2, 1 }, ("score", "float32")); // other keys: by value

        Assert.Equal((FetchOutcome.Ok, FetchOutcome.Ok), (exactStatus.Outcome, synthesizedStatus.Outcome));
        Assert.Equal(new double?[] { null, 0.5, double.PositiveInfinity }, Values(exact, 0, 3));
        Assert.Equal(new double?[] { double.PositiveInfinity, 0.5, null }, Values(synthesized, 0, 3));
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("feature", "s", "score", "type_changed", 2), (gap.Kind, gap.Source, gap.Field, gap.Reason, gap.Count));
        Assert.Equal("1", Assert.Single(gap.Examples));
        var stats = Assert.Single(replay.FieldStats);
        Assert.Equal((6L, 2L), (stats.Requested, stats.Missing));
    }

    [Fact]
    public void FieldsFetchedInSeparateCalls_AreSynthesizedTogether_WhenTheConfigurationRequestsThemInOneCall()
    {
        // The recording fetched a and b of key 1 in two calls; the replayed plan groups them into one. Neither recorded
        // call is this call, so both values come from the value index, and neither recorded call is consumed.
        var run = Run();
        run.FeatureCalls.Add(Feature(0, "[1]", ("a", "int64", "[10]")));
        run.FeatureCalls.Add(Feature(1, "[1]", ("b", "int64", "[20]")));
        var replay = RunReplay.Create(run, "/runs/0");

        var (merged, staging) = Fetch(replay, new long[] { 1 }, ("a", "int64"), ("b", "int64"));
        var (alone, single) = Fetch(replay, new long[] { 1 }, ("a", "int64")); // the first recorded call, exactly

        Assert.Equal(FetchOutcome.Ok, merged.Outcome);
        Assert.Equal((10L, 20L), (staging.Get(0, 0).AsInt64(), staging.Get(1, 0).AsInt64()));
        Assert.Equal((FetchOutcome.Ok, 10L), (alone.Outcome, single.Get(0, 0).AsInt64()));
        Assert.Empty(replay.Gaps);
        Assert.All(replay.FieldStats, s => Assert.Equal(0, s.Missing));
    }

    [Fact]
    public void ASynthesizedValue_IsTakenFromTheCallThatReturnedIt_NotFromALaterPartialCallThatLostTheKey()
    {
        var run = Run();
        run.FeatureCalls.Add(Feature(0, "[1]", ("a", "int64", "[7]")));
        var partial = Feature(1, "[1, 2]", ("a", "int64", "[null, 5]"));
        run.FeatureCalls.Add(new ReplayFeatureCall
        {
            Seq = partial.Seq, Source = partial.Source, Entity = partial.Entity, Shape = partial.Shape, Keys = partial.Keys, Status = "partial",
            FailedKeys = 1, Fields = partial.Fields, Values = partial.Values,
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var (_, staging) = Fetch(replay, new long[] { 2, 1 }, ("a", "int64"));

        Assert.Equal((5L, 7L), (staging.Get(0, 0).AsInt64(), staging.Get(0, 1).AsInt64()));
    }

    [Fact]
    public void ANodeWithoutARecordedCall_GetsACopy_WithoutTakingAnotherNodesCall()
    {
        var run = Run();
        run.CandidateCalls.Add(new ReplayCandidateCall
        {
            Seq = 0, Node = "recall", Source = "s", Limit = 2, Status = "ok", Ids = Json("[1, 2]"), Scores = new List<double> { 2, 1 },
        });
        var replay = RunReplay.Create(run, "/runs/0");

        // Added nodes ask first, with a smaller and a larger quota; then the recorded node asks for its own call.
        var (one, oneStatus) = Retrieve(replay, "added_small", 1);
        var (three, threeStatus) = Retrieve(replay, "added_large", 3);
        var (own, ownStatus) = Retrieve(replay, "recall", 2);

        Assert.True(oneStatus.Succeeded && threeStatus.Succeeded && ownStatus.Succeeded);
        Assert.Equal(new long[] { 1 }, one);
        Assert.Equal(new long[] { 1, 2 }, three);
        Assert.Equal(new long[] { 1, 2 }, own);
        var shortfall = Assert.Single(replay.Gaps); // the recording stopped at its limit of 2; the source may have had a third
        Assert.Equal(("candidate_source", "s", "limit_exceeds_recording", 1), (shortfall.Kind, shortfall.Source, shortfall.Reason, shortfall.Count));
        Assert.Equal(new[] { "added_large" }, shortfall.Examples);
    }

    [Fact]
    public void ANodeWithoutARecordedCall_OfASourceThatOnlyFailed_IsAGap()
    {
        var run = Run();
        run.CandidateCalls.Add(new ReplayCandidateCall { Seq = 0, Node = "recall", Source = "s", Limit = 2, Status = "timeout", Error = "timed out" });
        var replay = RunReplay.Create(run, "/runs/0");

        var (added, addedStatus) = Retrieve(replay, "added", 2);
        var (_, ownStatus) = Retrieve(replay, "recall", 2);

        Assert.True(addedStatus.Succeeded);
        Assert.Empty(added);
        Assert.False(ownStatus.Succeeded); // the recorded timeout is replayed for the node that had it
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("candidate_source", "not_recorded", "added"), (gap.Kind, gap.Reason, Assert.Single(gap.Examples)));
    }

    [Fact]
    public void ASharedModelInput_IsInEveryRowsDigest_Whole()
    {
        // Batch of 2 with a shared tensor of 4 values: its length divides by the batch size, but its leading dimension is
        // not the batch, so it is not split into rows. Changing half of it changes both rows' inputs.
        var run = Run();
        run.ModelCalls.Add(ModelCallOf(0, Call(2, NamedInput.FromFloat32("global", new[] { 4 }, new float[] { 10, 20, 30, 40 })), new long[] { 1, 2 }, 100, 101));
        var replay = RunReplay.Create(run, "/runs/0");

        var (status, scores) = Invoke(replay, Call(2, NamedInput.FromFloat32("global", new[] { 4 }, new float[] { 10, 20, 300, 400 })), 1, 2);

        Assert.True(status.Succeeded);
        Assert.All(scores, s => Assert.True(float.IsNaN(s)));
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("model", "inputs_changed", 2), (gap.Kind, gap.Reason, gap.Count));
    }

    [Fact]
    public void ModelInputDigests_IncludeShapeAndElementType_ButNotTheBatchDimension()
    {
        var flat = new float[] { 1, 2, 3, 4, 5, 6 };
        Assert.NotEqual(RowDigests(Call(1, NamedInput.FromFloat32("x", new[] { 1, 2, 3 }, flat))), RowDigests(Call(1, NamedInput.FromFloat32("x", new[] { 1, 6 }, flat))));
        Assert.NotEqual(RowDigests(Call(1, NamedInput.FromFloat32("x", new[] { 1, 1 }, new float[] { 7 }))),
            RowDigests(Call(1, NamedInput.FromInt64("x", new[] { 1, 1 }, new long[] { 7 }))));

        // Rows of a [batch, 2] tensor keep their digests in another batch: row 1 of a batch of 3 is row 0 of a batch of 1.
        var batchOfThree = RowDigests(Call(3, NamedInput.FromFloat32("x", new[] { 3, 2 }, new float[] { 1, 2, 3, 4, 5, 6 }), NamedInput.FromText("prompt", "p")));
        var batchOfOne = RowDigests(Call(1, NamedInput.FromFloat32("x", new[] { 1, 2 }, new float[] { 3, 4 }), NamedInput.FromText("prompt", "p")));
        Assert.Equal(batchOfThree[1], batchOfOne[0]);
        Assert.Equal(3, batchOfThree.Distinct().Count());
    }

    [Fact]
    public void ARebatchedModelCall_TakesEachRowFromTheRecordingWithTheSameEntityAndInputs()
    {
        // Entity 1 was scored twice with different inputs; the later recording is the one with the inputs the replay has.
        var run = Run();
        run.ModelCalls.Add(ModelCallOf(0, Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 10 })), new long[] { 1 }, 100));
        run.ModelCalls.Add(ModelCallOf(1, Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 20 })), new long[] { 1 }, 200));
        run.ModelCalls.Add(ModelCallOf(2, Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 30 })), new long[] { 2 }, 300));
        var replay = RunReplay.Create(run, "/runs/0");

        var (status, scores) = Invoke(replay, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 20, 30 })), 1, 2);
        var (_, changed) = Invoke(replay, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 40, 30 })), 1, 3);

        Assert.True(status.Succeeded);
        Assert.Equal(new float[] { 200, 300 }, scores);
        Assert.True(float.IsNaN(changed[0]) && float.IsNaN(changed[1]));
        Assert.Equal(new[] { ("inputs_changed", "1"), ("not_recorded", "3") }, replay.Gaps.Select(g => (g.Reason, Assert.Single(g.Examples))));
    }

    private const string CoreLib = "System.Private.CoreLib";
    private const string Tests = "StrataRec.UnitTests";

    [Theory]
    [InlineData("System.TimeoutException", CoreLib, "timeout", typeof(TimeoutException))]
    [InlineData("System.OperationCanceledException", CoreLib, "timeout", typeof(OperationCanceledException))]
    [InlineData("System.Threading.Tasks.TaskCanceledException", CoreLib, "timeout", typeof(TaskCanceledException))]
    [InlineData("System.InvalidOperationException", CoreLib, "error", typeof(InvalidOperationException))]
    [InlineData("StrataRec.UnitTests.Pipeline.RunReplayTests+TransientModelException", Tests, "error", typeof(TransientModelException))]
    [InlineData("StrataRec.UnitTests.Pipeline.RunReplayTests+NoMessageException", Tests, "error", typeof(ReplayedCallException))] // no (string) constructor
    [InlineData("No.Such.Exception", Tests, "error", typeof(ReplayedCallException))]
    [InlineData("System.String", CoreLib, "error", typeof(ReplayedCallException))] // not an exception
    [InlineData("System.InvalidOperationException", Tests, "error", typeof(ReplayedCallException))] // the name, but not of that assembly
    [InlineData("System.TimeoutException", Tests, "timeout", typeof(ReplayedCallException))] // core names too
    [InlineData("System.OperationCanceledException", "No.Such.Assembly", "timeout", typeof(ReplayedCallException))]
    [InlineData("System.InvalidOperationException, System.Private.CoreLib", CoreLib, "error", typeof(ReplayedCallException))] // a name, never a type spec
    [InlineData("StrataRec.UnitTests.Pipeline.RunReplayTests+BackendException`1", Tests, "error", typeof(ReplayedCallException))] // open generic
    public void AThrownModelCall_IsThrownAgain_AsItsRecordedType(string name, string assembly, string status, Type expected)
    {
        var thrown = Replayed(new ReplayTypeIdentity { Name = name, Assembly = assembly }, status);

        Assert.Equal(expected, thrown.GetType());
        Assert.Equal("backend said no", thrown.Message);
        if (thrown is ReplayedCallException replayed)
        {
            Assert.Equal(name, replayed.OriginalType);
        }
    }

    [Fact]
    public void AClosedGenericException_IsThrownAgain_WhenItsDefinitionAndArgumentsAreLoaded()
    {
        var identity = ReplayedExceptions.Identity(typeof(BackendException<KeyValuePair<string, int>>));
        Assert.Equal(("StrataRec.UnitTests.Pipeline.RunReplayTests+BackendException`1", Tests, 1), (identity.Name, identity.Assembly, identity.Args!.Count));

        Assert.IsType<BackendException<KeyValuePair<string, int>>>(Replayed(identity));

        // An argument that is not loaded, or that breaks the definition's constraint, is the type name and nothing else.
        var missing = new ReplayTypeIdentity { Name = identity.Name, Assembly = Tests, Args = new() { new() { Name = "No.Such.Type", Assembly = Tests } } };
        Assert.Equal("StrataRec.UnitTests.Pipeline.RunReplayTests+BackendException`1[No.Such.Type]",
            Assert.IsType<ReplayedCallException>(Replayed(missing)).OriginalType);
        var structOnly = ReplayedExceptions.Identity(typeof(StructException<int>));
        var violating = new ReplayTypeIdentity { Name = structOnly.Name, Assembly = structOnly.Assembly, Args = new() { ReplayedExceptions.Identity(typeof(string)) } };
        Assert.IsType<ReplayedCallException>(Replayed(violating));
    }

    [Fact]
    public void AnExceptionType_IsTheOneOfTheRecordedAssembly_AndNeverGuessedAmongNamesakes()
    {
        // Two loaded assemblies define the same full name; the client threw the second one's.
        var first = DefineException("ReplayNamesakeFirst", "Backend.TransientException");
        var second = DefineException("ReplayNamesakeSecond", "Backend.TransientException");
        Assert.IsType(second, Replayed(ReplayedExceptions.Identity(second)));
        Assert.IsType(first, Replayed(ReplayedExceptions.Identity(first)));

        // A namesake of a core exception is that namesake, not the core type (which would take the cancellation path).
        foreach (var core in new[] { "System.TimeoutException", "System.OperationCanceledException" })
        {
            var namesake = DefineException("ReplayCoreNamesake" + core.Length, core);
            Assert.IsType(namesake, Replayed(ReplayedExceptions.Identity(namesake), "timeout"));
        }

        // The same assembly name loaded twice, both with the type: which one the client threw cannot be told, so neither.
        DefineException("ReplayNamesakeTwice", "Backend.TransientException");
        var twice = DefineException("ReplayNamesakeTwice", "Backend.TransientException");
        Assert.Equal("Backend.TransientException", Assert.IsType<ReplayedCallException>(Replayed(ReplayedExceptions.Identity(twice))).OriginalType);
    }

    [Fact]
    public void ResolvingAnExceptionType_NeverLoadsAnAssembly()
    {
        // An assembly the test host can load by name (it ships next to the tests) but nothing here has loaded.
        const string unloaded = "Microsoft.VisualStudio.CodeCoverage.Shim";
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, unloaded + ".dll")));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == unloaded);

        var thrown = Replayed(new ReplayTypeIdentity { Name = "Microsoft.CodeCoverage.ShimException", Assembly = unloaded });

        Assert.IsType<ReplayedCallException>(thrown);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == unloaded);
    }

    /// <summary>What a replay throws for a one-row model call recorded as throwing <paramref name="type"/>.</summary>
    private static Exception Replayed(ReplayTypeIdentity? type, string status = "error")
    {
        var call = Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 10 }));
        var recorded = ModelCallOf(0, call, new long[] { 1 });
        var run = Run();
        run.ModelCalls.Add(new ReplayModelCall
        {
            Seq = 0, Node = recorded.Node, Model = "m", BatchSize = 1, Status = status, Error = "backend said no", Threw = true, ErrorType = type,
            Keys = recorded.Keys, InputDigests = recorded.InputDigests,
        });
        var replay = RunReplay.Create(run, "/runs/0");
        return Assert.ThrowsAny<Exception>(() => Invoke(replay, call, 1));
    }

    /// <summary>A dynamic assembly defining an exception type with a <c>(string message)</c> constructor.</summary>
    private static Type DefineException(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule(assemblyName).DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(Exception));
        var il = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(string) }).GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        il.Emit(OpCodes.Ret);
        return type.CreateType();
    }

    /// <summary>A client exception an operator might handle.</summary>
    public sealed class TransientModelException(string message) : Exception(message);

    /// <summary>An exception type the replay cannot create from a message.</summary>
    public sealed class NoMessageException : Exception;

    /// <summary>A generic client exception.</summary>
    public sealed class BackendException<T>(string message) : Exception(message);

    /// <summary>A generic exception whose argument must be a value type.</summary>
    public sealed class StructException<T>(string message) : Exception(message)
        where T : struct;

    internal static ModelCall Call(int batch, params NamedInput[] inputs) => new() { Model = "m", Inputs = inputs, BatchSize = batch };

    internal static string[] RowDigests(ModelCall call)
    {
        var order = ModelInputDigest.NameOrder(call);
        return Enumerable.Range(0, call.BatchSize).Select(r => ReplayJson.Hex(ModelInputDigest.Row(call, order, r))).ToArray();
    }

    /// <summary>A successful recorded call of model <c>m</c> scoring <paramref name="keys"/>, one <c>score</c> per row.</summary>
    internal static ReplayModelCall ModelCallOf(int seq, ModelCall call, long[] keys, params float[] scores) => new()
    {
        Seq = seq,
        Node = "ranker",
        Model = "m",
        BatchSize = call.BatchSize,
        Status = "ok",
        Keys = Json("[" + string.Join(",", keys) + "]"),
        InputDigests = RowDigests(call).ToList(),
        Outputs = new Dictionary<string, JsonElement> { ["score"] = JsonSerializer.SerializeToElement(scores) },
    };

    internal static (ModelStatus Status, float[] Scores) Invoke(RunReplay replay, ModelCall call, params long[] keys)
    {
        var writer = new ArrayOutputWriter();
        var status = replay.InvokeModel(call, writer, keys.Select(EntityKey.FromInt64).ToArray());
        return (status, writer.Floats.TryGetValue("score", out var scores) ? scores : Array.Empty<float>());
    }

    internal sealed class ArrayOutputWriter : IModelOutputWriter
    {
        public Dictionary<string, float[]> Floats { get; } = new();

        public Memory<float> GetFloat32(string name, int elementCount) => Floats[name] = new float[elementCount];

        public void SetText(string name, string text)
        {
        }
    }

    internal static (long[] Ids, StrataRec.Pipeline.Sources.CandidateSourceStatus Status) Retrieve(RunReplay replay, string node, int limit)
    {
        var buffer = new StrataRec.Pipeline.Sources.CandidateBuffer(limit, new RequestMemory());
        var query = new StrataRec.Pipeline.Sources.CandidateQuery { SourceName = "s", Limit = limit, Inputs = new RequestInputs(), RequestId = "r" };
        var status = replay.Retrieve(node, "s", query, buffer);
        return (buffer.Keys.ToArray().Select(k => k.Int64Value).ToArray(), status);
    }

    internal static ReplayRunRecord Run() => new()
    {
        PipelineId = "p",
        ManifestId = "m",
        Role = "primary",
        Sampling = new ReplaySamplingRecord { Logs = "none" },
        Result = new ReplayResultRecord { Succeeded = true },
    };

    internal static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A successful many-shape call of source <c>s</c> for entity <c>item</c>.</summary>
    internal static ReplayFeatureCall Feature(int seq, string keys, params (string Name, string Type, string Values)[] fields)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var bindings = new List<ReplayField>();
        foreach (var (name, type, json) in fields)
        {
            bindings.Add(new ReplayField { Name = name, Type = type });
            values[name] = Json(json);
        }

        return new ReplayFeatureCall
        {
            Seq = seq, Source = "s", Entity = "item", Shape = "many", Keys = Json(keys), Status = "ok", Fields = bindings, Values = values,
        };
    }

    /// <summary>Asks the replay for <paramref name="fields"/> of <paramref name="keys"/> from source <c>s</c>.</summary>
    internal static (FetchStatus Status, StagingBuffer Staging) Fetch(RunReplay replay, long[] keys, params (string Name, string Type)[] fields)
    {
        var bindings = new CompiledFieldBinding[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            bindings[i] = new CompiledFieldBinding(fields[i].Name, i, ColumnType.Parse(fields[i].Type));
        }

        var group = new FetchGroup
        {
            Index = 0,
            SourceName = "s",
            Entity = FeatureTestRig.Item,
            Supply = GroupSupply.Fetch(ScriptedConnector.Values((_, _) => throw new InvalidOperationException("a replay never calls the connector")), 50, 1000),
            Shape = KeyShape.Many,
            FeatureIndices = new int[fields.Length],
            Fields = bindings,
        };
        var staging = new StagingBuffer(bindings, keys.Length, new RequestMemory());
        var batch = new EntityKeyBatch { Shape = KeyShape.Many, CandidateKeys = keys.Select(EntityKey.FromInt64).ToArray() };
        return (replay.Replay(group, batch, bindings, staging), staging);
    }

    internal static double?[] Values(StagingBuffer staging, int field, int keys) =>
        Enumerable.Range(0, keys).Select(k => staging.Get(field, k) is { IsMissing: false } v ? v.AsFloat64() : (double?)null).ToArray();
}
