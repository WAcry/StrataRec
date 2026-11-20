using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Replay;
using StrataRec.Pipeline.Sources;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// How a replay answers a candidate call with query values (design 5.4, 13.4): the recorded call of the node, else a
/// call of another node with the same values, else another node's answer reported as <c>query_changed</c>; and the
/// recorded values are checked when the bundle is indexed (review 80).
/// </summary>
public sealed class CandidateQueryReplayTests
{
    [Fact]
    public void ANodeWithoutACall_GetsTheAnswerToTheSameValues_ElseAnotherReportedAsChanged()
    {
        var run = RunReplayTests.Run();
        run.CandidateCalls.Add(Recorded("a", "float64", RunReplayTests.Json("1"), "ok", 42, 43));
        run.CandidateCalls.Add(Recorded("b", "float64", RunReplayTests.Json("2"), "ok", 7));
        var replay = RunReplay.Create(run, "/runs/0");

        // A node the configuration added, querying with b's values: b's answer, though a's list is longer.
        Assert.Equal(new long[] { 7 }, Retrieve(replay, "added", ColumnType.Float64, ScalarValue.FromFloat64(2)).Ids);
        Assert.Empty(replay.Gaps);

        // Querying with values no call had: the longest list, reported (it was silent).
        Assert.Equal(new long[] { 42, 43 }, Retrieve(replay, "other", ColumnType.Float64, ScalarValue.FromFloat64(3)).Ids);
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("candidate_source", "s", "q", "query_changed", 1), (gap.Kind, gap.Source, gap.Field, gap.Reason, gap.Count));
        Assert.Equal("other", Assert.Single(gap.Examples));
    }

    [Fact]
    public void TheValuesCompare_AsTypedJson_MissingApartFromZero_AndAFailedCallIsAnsweredAsRecorded()
    {
        var parsed = ArtifactSerializer.Deserialize<ReplayCandidateCall>("""
            {"source":"s","node":"a","status":"ok","limit":10,"ids":[42],"scores":[1],"query":{"q":{"type":"float64?","value":null}}}
            """, "test");
        var run = RunReplayTests.Run();
        run.CandidateCalls.Add(parsed);
        run.CandidateCalls.Add(Recorded("b", "float64?", null, "ok", 42));
        run.CandidateCalls.Add(Recorded("c", "float64", RunReplayTests.Json("1e0"), "ok", 42));
        run.CandidateCalls.Add(Recorded("d", "int64", RunReplayTests.Json("1"), "ok", 42));
        run.CandidateCalls.Add(Recorded("e", "float64", RunReplayTests.Json("1"), "timeout"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.True(Retrieve(replay, "a", ColumnType.Float64.AsNullable(), ScalarValue.Missing).Succeeded); // null is missing
        Assert.True(Retrieve(replay, "b", ColumnType.Float64.AsNullable(), ScalarValue.FromFloat64(0)).Succeeded); // missing is not 0
        Assert.True(Retrieve(replay, "c", ColumnType.Float64, ScalarValue.FromFloat64(1)).Succeeded); // 1e0 is 1
        Assert.True(Retrieve(replay, "d", ColumnType.Float64, ScalarValue.FromFloat64(1)).Succeeded); // another type
        var failed = Retrieve(replay, "e", ColumnType.Float64, ScalarValue.FromFloat64(2));

        Assert.False(failed.Succeeded);
        Assert.Empty(failed.Ids);
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(3, gap.Count);
        Assert.Equal(new[] { "b", "d", "e" }, gap.Examples);
        Assert.Equal("candidate source s: its query value 'q' differs from the recorded one in 3 call(s), which were answered as recorded", gap.Description);
    }

    [Theory]
    [InlineData("nonsense", "1", "/runs/0/candidate_calls/0/query/q/type")]
    [InlineData("float64", """{"x":1}""", "/runs/0/candidate_calls/0/query/q/value")]
    [InlineData("list<float64>", """["a"]""", "/runs/0/candidate_calls/0/query/q/value")]
    [InlineData("list<float64,3>", "[1, 2]", "/runs/0/candidate_calls/0/query/q/value")]
    [InlineData("float64", "1e400", "/runs/0/candidate_calls/0/query/q/value")] // review 84: read as an infinity, it borrowed a real one's answer
    [InlineData("float32", "-1e400", "/runs/0/candidate_calls/0/query/q/value")]
    [InlineData("list<float64>", "[1, 1e400]", "/runs/0/candidate_calls/0/query/q/value")]
    public void ARecordedValueNotOfItsType_IsRejectedAtItsPointer(string type, string value, string pointer)
    {
        var run = RunReplayTests.Run();
        run.CandidateCalls.Add(Recorded("a", type, RunReplayTests.Json(value), "ok", 42));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(run, "/runs/0"));

        Assert.Equal(pointer, error.Pointer);
    }

    [Fact]
    public void AValueRecordedInAnotherSpelling_ComparesAsWhatItDecodesTo()
    {
        // " bool " with 1 and "bool" with true decode to the same value as the replayed true; "bool" with 0 is false and
        // answered the longest list. The raw JSON was compared, so the false call's answer was borrowed (review 82).
        var run = RunReplayTests.Run();
        run.CandidateCalls.Add(Recorded("a", " bool ", RunReplayTests.Json("1"), "ok", 1));
        run.CandidateCalls.Add(Recorded("b", "bool", RunReplayTests.Json("true"), "ok", 2));
        run.CandidateCalls.Add(Recorded("c", "bool", RunReplayTests.Json("0"), "ok", 3, 4, 5));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(new long[] { 1 }, Retrieve(replay, "a", ColumnType.Bool, ScalarValue.FromBool(true)).Ids);
        Assert.Equal(new long[] { 1 }, Retrieve(replay, "added", ColumnType.Bool, ScalarValue.FromBool(true)).Ids); // a true call, not c's
        Assert.Empty(replay.Gaps);
    }

    private static ReplayCandidateCall Recorded(string node, string type, JsonElement? value, string status, params long[] ids) => new()
    {
        Node = node,
        Source = "s",
        Limit = 10,
        Status = status,
        Ids = status == "ok" ? RunReplayTests.Json(JsonSerializer.Serialize(ids)) : null,
        Scores = status == "ok" ? [.. ids.Select(_ => 1.0)] : [],
        Query = new() { ["q"] = new ReplayTypedValue { Type = type, Value = value } },
    };

    private static (long[] Ids, bool Succeeded) Retrieve(RunReplay replay, string node, ColumnType type, ScalarValue value)
    {
        var memory = new RequestMemory();
        var buffer = new CandidateBuffer(10, memory);
        var query = new CandidateQuery
        {
            SourceName = "s", Limit = 10, Inputs = new RequestInputs(), RequestId = "r", Values = new(["q"], [type], [value]),
        };
        var result = replay.Retrieve(node, "s", query, buffer);
        var ids = buffer.Keys.ToArray().Select(k => k.Int64Value).ToArray();
        memory.Release();
        return (ids, result.Succeeded);
    }
}
