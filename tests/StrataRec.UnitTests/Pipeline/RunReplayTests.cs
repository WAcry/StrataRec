using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
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
            Connector = ScriptedConnector.Values((_, _) => throw new InvalidOperationException("a replay never calls the connector")),
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
