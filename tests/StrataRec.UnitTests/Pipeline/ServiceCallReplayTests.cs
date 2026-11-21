using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// How a replay answers a service call about no items, such as a recall's (design 13.4): by the digest of its inputs. The
/// node's own recorded call first, as recorded and consumed; else any successful call with the same inputs, consuming
/// nothing; else nothing, reported. A recorded answer answers only the inputs it was for.
/// </summary>
public sealed class ServiceCallReplayTests
{
    private static readonly RequestMemory Memory = new();

    [Fact]
    public void ANodesOwnCalls_AreReplayedInOrder_FailureIncluded_ThenTheSuccessfulAnswerToTheSameInputs()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "ok", 42));
        run.ServiceCalls.Add(Recorded("a", Query(1), "timeout"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("42", true), Retrieve(replay, "a", Query(1)));
        Assert.Equal(("", false), Retrieve(replay, "a", Query(1))); // the recorded timeout
        Assert.Equal(("42", true), Retrieve(replay, "a", Query(1))); // a third call of the same inputs
        Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void ANodeWithoutACall_GetsTheAnswerToTheSameInputs_AndTakesItFromNobody()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "ok", 42, 43));
        run.ServiceCalls.Add(Recorded("b", Query(2), "ok", 7));
        var replay = RunReplay.Create(run, "/runs/0");

        // A node the configuration added, asking what b asked: b's answer, though a's list is longer, and b keeps its own.
        Assert.Equal(("7", true), Retrieve(replay, "added", Query(2)));
        Assert.Equal(("7", true), Retrieve(replay, "b", Query(2)));
        Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void InputsNoCallHad_AreNotAnswered_AndReportedAsChangedForANodeThatHadACall()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "ok", 42));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("", false), Retrieve(replay, "a", Query(9)));
        Assert.Equal(("", false), Retrieve(replay, "other", Query(9)));

        Assert.Equal(
            new[] { ("recall", "s", (string?)null, "inputs_changed", 1, "a"), ("recall", "s", null, "not_recorded", 1, "other") },
            replay.Gaps.Select(g => (g.Kind, g.Source, g.Field, g.Reason, g.Count, g.Examples.Single())));
        Assert.Equal("recall s: 1 item(s) or call(s) have other inputs than recorded; not answered", replay.Gaps[0].Description);
    }

    [Fact]
    public void AMissingValue_IsNeitherZeroNorAbsent()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(null), "ok", 42));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.False(Retrieve(replay, "zero", Query(0)).Succeeded);
        Assert.False(Retrieve(replay, "none", new ServiceCall { Service = "s", Limit = 10 }).Succeeded);
        Assert.Equal(("42", true), Retrieve(replay, "missing", Query(null)));
    }

    [Fact]
    public void ARecordedFailure_AnswersOnlyItsOwnNode()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("e", Query(1), "error"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.False(Retrieve(replay, "added", Query(1)).Succeeded);
        Assert.Equal("not_recorded", Assert.Single(replay.Gaps).Reason);
        Assert.False(Retrieve(replay, "e", Query(1)).Succeeded); // as recorded: no new gap
        Assert.Single(replay.Gaps);
    }

    [Fact]
    public void FewerItemsAreThePrefix_AndMoreThanACallStoppedAtItsLimitHadIsAShortfall()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("full", Query(1, limit: 2), "ok", 1, 2));
        run.ServiceCalls.Add(Recorded("short", Query(2, limit: 5), "ok", 3, 4));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("1", true), Retrieve(replay, "full", Query(1, limit: 1)));
        Assert.Equal(("1,2", true), Retrieve(replay, "added", Query(1, limit: 3))); // the limit is not part of the inputs
        Assert.Equal(("3,4", true), Retrieve(replay, "short", Query(2, limit: 9))); // the service had no more

        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("recall", "limit_exceeds_recording", 1, "added"), (gap.Kind, gap.Reason, gap.Count, gap.Examples.Single()));
    }

    /// <summary>
    /// Two nodes asked the same query for 1 and 3 items; a node that replaces them asks for 3. It borrowed the first call
    /// with that digest, the shorter one, and reported a shortfall the bundle could answer (review of IO step 2).
    /// </summary>
    [Fact]
    public void ANodeWithoutACall_BorrowsTheSameInputsAnswerThatAnswersIt_NotTheFirst()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("one", Query(1, limit: 1), "ok", 7));
        run.ServiceCalls.Add(Recorded("three", Query(1, limit: 3), "ok", 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("7,8,9", true), Retrieve(replay, "added", Query(1, limit: 3)));
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// A recorded call that asked for no items says nothing about the items the service has: it does not answer a call
    /// asking for 3, which another call with the same inputs answers, nor, alone, the same node asking for 3 now (a shortfall
    /// the recording cannot tell, <c>limit_exceeds_recording</c>). It answered both with no items and no gap (verification
    /// of IO step 2).
    /// </summary>
    [Fact]
    public void ACallThatAskedForNoItems_AnswersNoCallThatAsksForSome()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("none", Query(1, limit: 0), "ok"));
        run.ServiceCalls.Add(Recorded("three", Query(1, limit: 3), "ok", 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("7,8,9", true), Retrieve(replay, "added", Query(1, limit: 3)));
        Assert.Empty(replay.Gaps);

        var alone = RunReplayTests.Run();
        alone.ServiceCalls.Add(Recorded("none", Query(1, limit: 0), "ok"));
        var raised = RunReplay.Create(alone, "/runs/0");

        Assert.Equal(("", true), Retrieve(raised, "none", Query(1, limit: 3)));
        var gap = Assert.Single(raised.Gaps);
        Assert.Equal(("limit_exceeds_recording", 3), (gap.Reason, gap.Count));
    }

    /// <summary>
    /// Of two partial answers with as much of what a call reads, the one that can tell its items is borrowed: both answered
    /// text <c>a</c> and no items, the second asked for 3 (the service had none), the first for 0 (it says nothing). The
    /// first was borrowed and the replay reported 3 items it could not tell besides the missing <c>b</c> (verification 2 of
    /// IO step 2). Since the review of IO step 3 a call reading outputs is digested with its limit, so the first is not even
    /// a candidate: its text may say it was asked for none.
    /// </summary>
    [Fact]
    public void OfPartialAnswersAsGood_TheOneThatCanTellItsItems_IsBorrowed()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("none", Reading(Query(1, limit: 0), "a"), "ok", texts: new() { ["a"] = "A" }));
        run.ServiceCalls.Add(Recorded("three", Reading(Query(1, limit: 3), "a"), "ok", texts: new() { ["a"] = "A" }));
        var replay = RunReplay.Create(run, "/runs/0");
        var asked = Reading(Query(1, limit: 3), "a", "b");

        var status = replay.Call(ServiceCaller.OfNode("added"), new IoTarget(IoRoles.Recall, "s"), asked, new ServiceResponse(Memory, asked.Limit), out _);

        Assert.True(status.Succeeded);
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("not_recorded", "b"), (gap.Reason, gap.Field));
    }

    /// <summary>The same for outputs: a node reading <c>b</c> borrows the call with the same inputs that has it.</summary>
    [Fact]
    public void ANodeWithoutACall_BorrowsTheSameInputsAnswerWithTheOutputItReads()
    {
        var run = RunReplayTests.Run();
        var asked = Query(1);
        run.ServiceCalls.Add(Recorded("first", Reading(asked, "a"), "ok", texts: new() { ["a"] = "A" }));
        run.ServiceCalls.Add(Recorded("second", Reading(asked, "b"), "ok", texts: new() { ["b"] = "B" }));
        var replay = RunReplay.Create(run, "/runs/0");
        var response = new ServiceResponse(Memory, asked.Limit);

        var status = replay.Call(ServiceCaller.OfNode("added"), new IoTarget(IoRoles.Recall, "s"), Reading(asked, "b"), response, out _);

        Assert.True(status.Succeeded);
        Assert.True(response.TryGetText("b", out var text));
        Assert.Equal("B", text);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// A call that reads a named output more or less than its recording, with the same inputs and limit, still meets its
    /// recording: its list is answered, and an output the recording lacks is a <c>not_recorded</c> gap. The limit in the
    /// digest made such a call another call, its inputs reported changed (review of IO step 4).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACallReadingAnOutputMoreOrLess_MeetsItsRecording(bool recordedReadsOutput)
    {
        var original = recordedReadsOutput ? Reading(Query(1, limit: 2), "summary") : Query(1, limit: 2);
        var replayed = recordedReadsOutput ? Query(1, limit: 2) : Reading(Query(1, limit: 2), "summary");
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("n", original, "ok", texts: recordedReadsOutput ? new() { ["summary"] = "A" } : new(), 7, 8));
        var replay = RunReplay.Create(run, "/runs/0");
        var response = new ServiceResponse(Memory, 2);

        var status = replay.Call(ServiceCaller.OfNode("n"), new IoTarget(IoRoles.Recall, "s"), replayed, response, out _);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal([7L, 8L], response.Keys.ToArray().Select(k => k.Int64Value));
        if (recordedReadsOutput)
        {
            Assert.Empty(replay.Gaps);
        }
        else
        {
            var gap = Assert.Single(replay.Gaps);
            Assert.Equal(("summary", "not_recorded"), (gap.Field, gap.Reason));
        }
    }

    /// <summary>
    /// A named output recorded for another limit is not replayed (it may say how many items it was asked for): the call
    /// gets the start of the recorded list, and the output is a <c>limit_changed</c> gap.
    /// </summary>
    [Fact]
    public void AnOutputRecordedForAnotherLimit_IsALimitChangedGap()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("n", Reading(Query(1, limit: 3), "summary"), "ok", texts: new() { ["summary"] = "asked 3" }, 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");
        var asked = Reading(Query(1, limit: 1), "summary");
        var response = new ServiceResponse(Memory, asked.Limit);

        var status = replay.Call(ServiceCaller.OfNode("n"), new IoTarget(IoRoles.Recall, "s"), asked, response, out _);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal([7L], response.Keys.ToArray().Select(k => k.Int64Value));
        Assert.False(response.TryGetText("summary", out _));
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("summary", "limit_changed"), (gap.Field, gap.Reason));
    }

    /// <summary>
    /// The items a service added past the call's limit are replayed for that limit, as the node saw them; a call of a
    /// smaller limit answered from the start of the list has none (review of IO step 4: they were not recorded).
    /// </summary>
    [Theory]
    [InlineData(2, 3)]
    [InlineData(1, 0)]
    public void TheItemsAServiceDroppedPastTheLimit_AreReplayedForThatLimit(int limit, int dropped)
    {
        var recorded = Query(1, limit: 2);
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Node = "n",
            Service = "s",
            Role = IoRoles.Recall,
            Limit = 2,
            Status = "ok",
            InputDigests = [ReplayJson.Hex(ServiceInputDigest.Call(recorded, ServiceInputDigest.NameOrder(recorded)))],
            Ids = RunReplayTests.Json("[7,8]"),
            Scores = [1.0, 1.0],
            Dropped = 3,
        });
        var replay = RunReplay.Create(run, "/runs/0");
        var response = new ServiceResponse(Memory, limit);

        Assert.True(replay.Call(ServiceCaller.OfNode("n"), new IoTarget(IoRoles.Recall, "s"), Query(1, limit), response, out _).Succeeded);

        Assert.Equal(dropped, response.Dropped);
    }

    /// <summary>
    /// A failure or a cut-off recorded at another limit does not end a call reading outputs that has an answer of its own
    /// limit (review of IO step 4: the first call of the same inputs was taken, whatever its limit).
    /// </summary>
    [Theory]
    [InlineData("error", false)]
    [InlineData("cancelled", true)]
    public void AFailureRecordedAtAnotherLimit_DoesNotEndACallReadingOutputs_ThatHasItsOwnAnswer(string status, bool cutOff)
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(With(Recorded("n", Reading(Query(1, limit: 1), "summary"), status), threw: cutOff));
        run.ServiceCalls.Add(Recorded("n", Reading(Query(1, limit: 3), "summary"), "ok", texts: new() { ["summary"] = "three" }, 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");
        var asked = Reading(Query(1, limit: 3), "summary");
        var response = new ServiceResponse(Memory, asked.Limit);

        var answered = replay.Call(ServiceCaller.OfNode("n"), new IoTarget(IoRoles.Recall, "s"), asked, response, out _);

        Assert.True(answered.Succeeded, answered.Error);
        Assert.Equal([7L, 8L, 9L], response.Keys.ToArray().Select(k => k.Int64Value));
        Assert.True(response.TryGetText("summary", out var text));
        Assert.Equal("three", text);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// A call takes the node's recording of its own limit first, though recorded later. A failure answers only a call of
    /// its limit: of another, the service may have failed only past some limit (review of IO step 4b), so a success that
    /// another node recorded at the call's limit answers it, and with none the call is a <c>limit_changed</c> gap, each time.
    /// </summary>
    [Fact]
    public void AFailure_AnswersOnlyACallOfItsLimit()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("n", Query(1, limit: 1), "error"));
        run.ServiceCalls.Add(Recorded("n", Query(1, limit: 3), "ok", 7, 8, 9));
        var replay = RunReplay.Create(run, "/runs/0");
        Assert.Equal(("7,8,9", true), Retrieve(replay, "n", Query(1, limit: 3))); // its own limit's, though recorded second
        Assert.Equal(("", false), Retrieve(replay, "n", Query(1, limit: 1))); // the failure, as recorded
        Assert.Empty(replay.Gaps);

        var larger = RunReplayTests.Run();
        larger.ServiceCalls.Add(Recorded("n", Query(1, limit: 11), "error"));
        larger.ServiceCalls.Add(Recorded("m", Query(1, limit: 2), "ok", 7, 8));
        var smaller = RunReplay.Create(larger, "/runs/0");
        Assert.Equal(("7,8", true), Retrieve(smaller, "n", Query(1, limit: 2))); // m's, not n's failure at 11
        Assert.Empty(smaller.Gaps);

        foreach (var asked in new[] { Query(1, limit: 3), Reading(Query(1, limit: 3), "summary") })
        {
            var failed = RunReplayTests.Run();
            failed.ServiceCalls.Add(Recorded("n", Reading(Query(1, limit: 1), "summary"), "error"));
            failed.ServiceCalls.Add(Recorded("n", Query(1, limit: 1), "error"));
            var other = RunReplay.Create(failed, "/runs/0");
            Assert.Equal(("", false), Retrieve(other, "n", asked));
            Assert.Equal(("", false), Retrieve(other, "n", asked));
            Assert.All(other.Gaps, gap => Assert.Equal((null, "limit_changed"), (gap.Field, gap.Reason)));
            Assert.Equal(2, other.Gaps.Sum(g => g.Count));
        }
    }

    /// <summary>
    /// A recording of another limit that cannot answer the call is not used up by it: a later call of its limit still gets
    /// it (review of IO step 4b: it was consumed, and that call got the failure recorded after it).
    /// </summary>
    [Fact]
    public void ARecordingThatCannotAnswerTheCall_IsLeftForACallOfItsLimit()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("n", Reading(Query(1, limit: 1), "summary"), "ok", texts: new() { ["summary"] = "one" }, 7));
        run.ServiceCalls.Add(Recorded("n", Reading(Query(1, limit: 1), "summary"), "error"));
        run.ServiceCalls.Add(Recorded("m", Reading(Query(1, limit: 2), "summary"), "ok", texts: new() { ["summary"] = "two" }, 7, 8));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.Equal(("7,8", "two"), Summary(replay, Reading(Query(1, limit: 2), "summary"))); // m's: n's of limit 1 cannot answer
        Assert.Equal(("7", "one"), Summary(replay, Reading(Query(1, limit: 1), "summary"))); // n's first, still there
        Assert.Empty(replay.Gaps);

        static (string Ids, string? Text) Summary(RunReplay replay, ServiceCall call)
        {
            var response = new ServiceResponse(Memory, call.Limit);
            var status = replay.Call(ServiceCaller.OfNode("n"), new IoTarget(IoRoles.Recall, "s"), call, response, out _);
            Assert.True(status.Succeeded, status.Error);
            return (string.Join(",", response.Keys.ToArray().Select(k => k.Int64Value)), response.TryGetText("summary", out var text) ? text : null);
        }
    }

    /// <summary>
    /// The generation details of a recorded call (tokens, truncation) describe what it generated for its limit: a call of
    /// another limit answered from the start of its list does not get them (review of IO step 4).
    /// </summary>
    [Theory]
    [InlineData(3, 20, true)]
    [InlineData(1, null, false)]
    public void TheGenerationDetails_AreReplayedOnlyForTheRecordedLimit(int limit, int? tokens, bool truncated)
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(With(Recorded("n", Query(1, limit: 3), "ok", 7, 8, 9), outputTokens: 20, truncated: true));
        var replay = RunReplay.Create(run, "/runs/0");
        var response = new ServiceResponse(Memory, limit);

        var status = replay.Call(ServiceCaller.OfNode("n"), new IoTarget(IoRoles.Recall, "s"), Query(1, limit), response, out _);

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal((tokens, truncated), (status.OutputTokens, status.Truncated));
    }

    /// <summary>
    /// A service drops items only past a full list, and a recording holds the count only when there were some: any other
    /// count is no answer a service gave, rejected at its pointer (review of IO step 4); nor is one of a failure, even of
    /// a call that asked for no items (review of IO step 4b).
    /// </summary>
    [Theory]
    [InlineData(-1, 2, 2, "ok")]
    [InlineData(0, 2, 2, "ok")]
    [InlineData(3, 1, 2, "ok")]
    [InlineData(1, 0, 0, "error")]
    public void ADroppedCountNoServiceCouldGive_IsRejectedAtItsPointer(int dropped, int ids, int limit, string status)
    {
        var run = RunReplayTests.Run();
        long[] answered = ids switch { 2 => [7, 8], 1 => [7], _ => [] };
        run.ServiceCalls.Add(With(Recorded("n", Query(1, limit: limit), status, answered), dropped: dropped));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(run, "/runs/0"));

        Assert.Equal("/runs/0/service_calls/0/dropped", error.Pointer);
    }

    /// <summary>
    /// A feature source's call (IO step 5) is recorded with its source and no node: its failure answers the source, not a
    /// node whose id is the source's name.
    /// </summary>
    [Fact]
    public void ASourcesRecordedFailure_AnswersTheSource_NotANodeOfTheSameName()
    {
        var run = RunReplayTests.Run();
        var failed = Recorded("x", Query(1, limit: 0), "error");
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Source = "x", Service = failed.Service, Role = IoRoles.Fetch, Limit = failed.Limit, Status = failed.Status, InputDigests = failed.InputDigests,
        });
        var replay = RunReplay.Create(run, "/runs/0");
        var response = new ServiceResponse(Memory, 0);

        Assert.False(replay.Call(ServiceCaller.OfNode("x"), new IoTarget(IoRoles.Recall, "s"), Query(1, limit: 0), response, out _).Succeeded);
        Assert.Equal("not_recorded", Assert.Single(replay.Gaps).Reason); // the node has no call of its own
        Assert.False(replay.Call(ServiceCaller.OfSource("x"), new IoTarget(IoRoles.Fetch, "s"), Query(1, limit: 0), new ServiceResponse(Memory, 0), out _).Succeeded);
        Assert.Single(replay.Gaps); // the source's, as recorded
    }

    /// <summary>
    /// A node whose id spells a source's key (a plan built without the plan compiler's id checks) is still another caller
    /// than the source (review of IO step 5: the source took the node's recorded failure).
    /// </summary>
    [Fact]
    public void ANodeWhoseIdSpellsASourcesKey_IsNotTheSource()
    {
        var run = RunReplayTests.Run();
        var failed = Recorded("source:x", Query(1, limit: 0), "error");
        var answered = Recorded("x", Query(1, limit: 0), "ok");
        run.ServiceCalls.Add(failed);
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Source = "x", Service = answered.Service, Role = IoRoles.Fetch, Limit = 0, Status = "ok", InputDigests = answered.InputDigests,
            Ids = answered.Ids, Scores = answered.Scores,
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var status = replay.Call(ServiceCaller.OfSource("x"), new IoTarget(IoRoles.Fetch, "s"), Query(1, limit: 0), new ServiceResponse(Memory, 0), out _);

        Assert.True(status.Succeeded, status.Error);
        Assert.Empty(replay.Gaps);
    }

    [Fact]
    public void ACallOfBothANodeAndASource_IsRejectedAtItsSource()
    {
        var run = RunReplayTests.Run();
        var call = Recorded("n", Query(1), "ok", 1);
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Node = "n", Source = "n", Service = call.Service, Role = call.Role, Limit = call.Limit, Status = call.Status, InputDigests = call.InputDigests,
            Ids = call.Ids, Scores = call.Scores,
        });

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(run, "/runs/0"));

        Assert.Equal("/runs/0/service_calls/0/source", error.Pointer);
    }

    /// <summary>A node's recorded failure, replayed once, leaves its next call of the same inputs unrecorded: not "inputs changed".</summary>
    [Fact]
    public void ANodesCallsOfTheSameInputsUsedUp_AreNotRecorded_NotChanged()
    {
        var run = RunReplayTests.Run();
        run.ServiceCalls.Add(Recorded("a", Query(1), "error"));
        var replay = RunReplay.Create(run, "/runs/0");

        Assert.False(Retrieve(replay, "a", Query(1)).Succeeded); // as recorded
        Assert.False(Retrieve(replay, "a", Query(1)).Succeeded);

        Assert.Equal(("not_recorded", 1), (Assert.Single(replay.Gaps).Reason, replay.Gaps[0].Count));
    }

    [Fact]
    public void ScoresNotOnePerId_AreRejectedAtTheirPointer()
    {
        var run = RunReplayTests.Run();
        var call = Recorded("a", Query(1), "ok", 1, 2);
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Node = call.Node, Service = call.Service, Role = call.Role, Limit = call.Limit, Status = call.Status, InputDigests = call.InputDigests,
            Ids = call.Ids, Scores = [1.0],
        });

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(run, "/runs/0"));

        Assert.Equal("/runs/0/service_calls/0/scores", error.Pointer);
    }

    /// <summary>A recall's call with one query value, <c>q</c>, as <c>MultiSourceRecall@1</c> sends it; null is missing.</summary>
    private static ServiceCall Query(double? q, int limit = 10) => new()
    {
        Service = "s",
        Limit = limit,
        Inputs = new[] { ServiceInputs.Create("q", ColumnType.Float64.AsNullable(), q is { } v ? ScalarValue.FromFloat64(v) : ScalarValue.Missing, Memory) },
    };

    private static ReplayServiceCall Recorded(string node, ServiceCall call, string status, params long[] ids) => Recorded(node, call, status, null, ids);

    private static ReplayServiceCall Recorded(string node, ServiceCall call, string status, Dictionary<string, string>? texts, params long[] ids) => new()
    {
        Texts = texts ?? new(),
        Node = node,
        Service = "s",
        Role = IoRoles.Recall,
        Limit = call.Limit,
        Status = status,
        InputDigests = [ReplayJson.Hex(ServiceInputDigest.Call(call, ServiceInputDigest.NameOrder(call)))],
        Requested = [.. call.Outputs.ToArray()],
        Ids = status == "ok" ? RunReplayTests.Json(JsonSerializer.Serialize(ids)) : null,
        Scores = status == "ok" ? [.. ids.Select(_ => 1.0)] : [],
    };

    /// <summary>A recorded call with what <see cref="Recorded(string, ServiceCall, string, long[])"/> leaves out.</summary>
    private static ReplayServiceCall With(ReplayServiceCall c, bool threw = false, int? outputTokens = null, bool? truncated = null, int? dropped = null) => new()
    {
        Node = c.Node, Service = c.Service, Role = c.Role, Limit = c.Limit, Status = c.Status, InputDigests = c.InputDigests, Requested = c.Requested, Ids = c.Ids,
        Scores = c.Scores, Texts = c.Texts, Threw = threw, OutputTokens = outputTokens, Truncated = truncated, Dropped = dropped,
    };

    private static ServiceCall Reading(ServiceCall call, params string[] outputs) => new()
    {
        Service = call.Service, Inputs = call.Inputs, Limit = call.Limit, Outputs = outputs,
    };

    /// <summary>The ids answered, comma-separated, and whether the call succeeded.</summary>
    private static (string Ids, bool Succeeded) Retrieve(RunReplay replay, string node, ServiceCall call)
    {
        var response = new ServiceResponse(Memory, call.Limit);
        var status = replay.Call(ServiceCaller.OfNode(node), new IoTarget(IoRoles.Recall, "s"), call, response, out _);
        return (string.Join(",", response.Keys.ToArray().Select(k => k.Int64Value)), status.Succeeded);
    }
}
