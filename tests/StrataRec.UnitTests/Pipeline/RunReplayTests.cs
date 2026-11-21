using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Connectors;
using StrataRec.Features.Runtime;
using StrataRec.Core.Diagnostics;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Replay;
using StrataRec.UnitTests.Features;
using StrataRec.Core.Services;

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
    public void RecordedLists_AreWrittenIntoListFieldsOfEveryElementType_AndAFloat32ListThatWouldOverflow_IsATypeChangedGap()
    {
        // Each element follows the scalar rule: int64 into int32 when it fits, numbers into float32 unless they overflow.
        var run = Run();
        run.FeatureCalls.Add(Feature(0, "[1, 2]",
            ("b", "list<bool>", "[[true, false], []]"),
            ("i", "list<int32>", "[[1, -2], [3]]"),
            ("f", "list<float64>", "[[0.5, \"NaN\"], [1e100]]"),
            ("w", "list<float32>", "[[0.25], [1]]")));
        var replay = RunReplay.Create(run, "/runs/0");

        var (status, staging) = Fetch(replay, new long[] { 1, 2 },
            ("b", "list<bool>"), ("i", "list<int64>"), ("f", "list<float32>"), ("w", "list<float64>"));

        Assert.Equal(FetchOutcome.Ok, status.Outcome);
        Assert.Equal(new[] { true, false }, staging.Get(0, 0).AsBoolList().ToArray());
        Assert.True(staging.Get(0, 1).AsBoolList().IsEmpty);
        Assert.Equal(new long[] { 1, -2 }, staging.Get(1, 0).AsInt64List().ToArray());
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 0.5, double.NaN }), staging.Get(2, 0)); // NaN stays NaN
        Assert.True(staging.Get(2, 1).IsMissing); // 1e100 would become +∞ in float32
        Assert.Equal(ScalarValue.FromFloat64List(new[] { 0.25 }), staging.Get(3, 0));
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("f", "type_changed", 1), (gap.Field, gap.Reason, gap.Count));
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
            Seq = partial.Seq, Source = partial.Source, Entity = partial.Entity, Keys = partial.Keys, Status = "partial",
            FailedKeys = 1, Fields = partial.Fields, Values = partial.Values,
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var (_, staging) = Fetch(replay, new long[] { 2, 1 }, ("a", "int64"));

        Assert.Equal((5L, 7L), (staging.Get(0, 0).AsInt64(), staging.Get(0, 1).AsInt64()));
    }

    /// <summary>
    /// Two nodes call one model with the same rows and inputs, each reading its own output. Swapped in the replay, the one
    /// that asks first took the other's recording (matched by items and inputs alone) and lacked its own output (review of
    /// IO step 2): a node's own recorded call answers it, another node's only when it has what the call reads.
    /// </summary>
    [Fact]
    public void TwoNodesCallingOneModelWithTheSameRows_EachGetTheirOwnOutputs_InEitherOrder()
    {
        var run = Run();
        var call = Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 2 }));
        ReplayServiceCall Of(int seq, string node, string output, float[] values) => new()
        {
            Seq = seq, Node = node, Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(call).ToList(),
            Outputs = new Dictionary<string, ReplayOutput> { [output] = ReplayJson.Tensor(TensorValues.Of<float>(values)) },
        };
        run.ServiceCalls.Add(Of(0, "click", "score", [10, 20]));
        run.ServiceCalls.Add(Of(1, "extra", "p_extra", [5, 6]));
        var replay = RunReplay.Create(run, "/runs/0");
        EntityKey[] keys = [EntityKey.FromInt64(1), EntityKey.FromInt64(2)];

        var extraOut = new ArrayOutputWriter();
        var clickOut = new ArrayOutputWriter();
        var extraStatus = replay.InvokeModel(call.Reading("p_extra"), extraOut, keys, "extra");
        var clickStatus = replay.InvokeModel(call, clickOut, keys, "click");
        var addedOut = new ArrayOutputWriter();
        var added = replay.InvokeModel(call.Reading("p_extra"), addedOut, keys, "added"); // borrows the call that has it

        Assert.True(extraStatus.Succeeded && clickStatus.Succeeded && added.Succeeded);
        Assert.Equal(new float[] { 5, 6 }, extraOut.Floats["p_extra"]);
        Assert.Equal(new float[] { 10, 20 }, clickOut.Floats["score"]);
        Assert.Equal(new float[] { 5, 6 }, addedOut.Floats["p_extra"]);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// A recorded call of the same items and inputs answers as recorded when it lacks an output the call reads now: its
    /// text and the order of repeated items stay, and only the new output is a gap. It was synthesized item by item, which
    /// lost the text (a gap, and a failed call) and took the first row for each repeat (verification of IO step 2).
    /// </summary>
    [Fact]
    public void AnExactRecording_ThatLacksAnOutputReadNow_AnswersAsRecorded_WithAGapOfThatOutputOnly()
    {
        var run = Run();
        var text = TextCall("p");
        run.ServiceCalls.Add(TextCallOf(0, text, new long[] { 1 }, "ONE"));
        var repeated = Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 1 }));
        run.ServiceCalls.Add(ModelCallOf(1, repeated, new long[] { 5, 5 }, 10, 99));
        var replay = RunReplay.Create(run, "/runs/0");

        var answered = new ArrayOutputWriter();
        var status = replay.InvokeModel(text.Reading("answer", "new_score"), answered, [EntityKey.FromInt64(1)], "annotator");
        var scores = new ArrayOutputWriter();
        var scored = replay.InvokeModel(repeated.Reading("score", "new"), scores, [EntityKey.FromInt64(5), EntityKey.FromInt64(5)]);

        Assert.True(status.Succeeded && scored.Succeeded);
        Assert.Equal("ONE", answered.Texts["answer"]);
        Assert.Equal(new float[] { 10, 99 }, scores.Floats["score"]);
        Assert.Equal(new[] { ("new", "not_recorded"), ("new_score", "not_recorded") }, replay.Gaps.Select(g => (g.Field!, g.Reason)).OrderBy(g => g.Item1, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A call about items that reads only its status (a check of each candidate) has no answer when none of its items is
    /// recorded with these inputs: it was synthesized as a success, so the caller kept candidates no recording vouched for.
    /// </summary>
    [Fact]
    public void ACallAboutItemsReadingOnlyItsStatus_FailsWhenNoItemIsRecorded()
    {
        var replay = RunReplay.Create(Run(), "/runs/0");
        var check = new ServiceCall { Service = "m", BatchSize = 1, Inputs = new[] { NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 1 }) } };

        var status = replay.InvokeModel(check, new ArrayOutputWriter(), [EntityKey.FromInt64(1)]);

        Assert.False(status.Succeeded);
        Assert.Equal("not_recorded", Assert.Single(replay.Gaps).Reason);
    }

    /// <summary>
    /// A row's digest covers its own values: built from a longer (pooled) buffer, an input is its shape's elements, so the
    /// slack does not shift the rows (a buffer of 4 for a shape of [2] made each row two values wide: row 41's digest covered
    /// both rows' values and row 42's the slack, verification of IO step 2).
    /// </summary>
    [Fact]
    public void ARowsDigest_IsOfItsOwnValues_WhateverTheBufferHoldsBeyondTheShape()
    {
        var run = Run();
        run.ServiceCalls.Add(ModelCallOf(0, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 2, 90, 99 })), new long[] { 41, 42 }, 10, 20));
        var replay = RunReplay.Create(run, "/runs/0");

        var (status, scores) = Invoke(replay, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 3 })), 41, 42);

        Assert.True(status.Succeeded);
        Assert.Equal(10f, scores[0]);
        Assert.True(float.IsNaN(scores[1]));
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("inputs_changed", "42"), (gap.Reason, Assert.Single(gap.Examples)));
    }

    /// <summary>
    /// An output a recorded call has, not of whole rows, exempts from its gap only the rows of that call: the node failed on
    /// it when recorded. A row of another call that has no such output is a gap of it (it was exempt by the name alone).
    /// </summary>
    [Fact]
    public void AMalformedRecordedOutput_ExemptsOnlyItsOwnRows_FromTheOutputsGap()
    {
        var run = Run();
        var first = Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 2 }));
        var malformed = ModelCallOf(0, first, new long[] { 1, 2 }, 10, 20);
        malformed.Outputs["extra"] = new ReplayOutput { Type = "float32", Values = JsonSerializer.SerializeToElement(new float[] { 1, 2, 3 }) };
        run.ServiceCalls.Add(malformed);
        run.ServiceCalls.Add(ModelCallOf(1, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 3, 4 })), new long[] { 3, 4 }, 30, 40));
        var replay = RunReplay.Create(run, "/runs/0");

        var status = replay.InvokeModel(
            Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 3 })).Reading("score", "extra"), new ArrayOutputWriter(),
            [EntityKey.FromInt64(1), EntityKey.FromInt64(3)]);

        Assert.True(status.Succeeded);
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("extra", "not_recorded", "3"), (gap.Field, gap.Reason, Assert.Single(gap.Examples)));
    }

    /// <summary>
    /// A rebatched call of an item the recording has, reading only an output no recorded call has: a gap of that output,
    /// not of the item (it was reported without its output's name, review of IO step 2).
    /// </summary>
    [Fact]
    public void ARebatchedCallReadingOnlyAnUnrecordedOutput_IsAGapOfThatOutput()
    {
        var run = Run();
        run.ServiceCalls.Add(ModelCallOf(0, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 2 })), new long[] { 1, 2 }, 10, 20));
        var replay = RunReplay.Create(run, "/runs/0");

        var status = replay.InvokeModel(Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 2 })).Reading("p_extra"), new ArrayOutputWriter(), [EntityKey.FromInt64(2)]);

        Assert.False(status.Succeeded);
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("p_extra", "not_recorded", "2"), (gap.Field, gap.Reason, Assert.Single(gap.Examples)));
    }

    [Fact]
    public void AnOutputTheRecordedCallDidNotAskFor_IsAGapOfThatOutput()
    {
        // Only the outputs a call asks for are recorded: a configuration that reads another one finds none in the bundle.
        var run = Run();
        var recorded = Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 1, 2 }));
        run.ServiceCalls.Add(ModelCallOf(0, recorded, new long[] { 1, 2 }, 10, 20));
        var replay = RunReplay.Create(run, "/runs/0");
        var asking = new ServiceCall { Service = "m", Inputs = recorded.Inputs, BatchSize = 2, Outputs = new[] { "score", "p_extra" } };
        var writer = new ArrayOutputWriter();

        var status = replay.InvokeModel(asking, writer, new[] { EntityKey.FromInt64(1), EntityKey.FromInt64(2) });

        Assert.True(status.Succeeded); // the operator reports the output it lacks, as for a model that does not return it
        Assert.Equal(new float[] { 10, 20 }, writer.Floats["score"]);
        Assert.False(writer.Arrays.ContainsKey("p_extra"));
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("model", "m", "p_extra", "not_recorded", 2), (gap.Kind, gap.Source, gap.Field, gap.Reason, gap.Count));
        Assert.Equal(["1", "2"], gap.Examples);
        Assert.Equal("model m: output 'p_extra' is not in the recording for 2 item(s) or call(s)", gap.Description);
    }

    [Fact]
    public void ASharedModelInput_IsInEveryRowsDigest_Whole()
    {
        // Batch of 2 with a shared tensor of 4 values: its length divides by the batch size, but its leading dimension is
        // not the batch, so it is not split into rows. Changing half of it changes both rows' inputs.
        var run = Run();
        run.ServiceCalls.Add(ModelCallOf(0, Call(2, NamedInput.FromFloat32("global", new[] { 4 }, new float[] { 10, 20, 30, 40 })), new long[] { 1, 2 }, 100, 101));
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
        run.ServiceCalls.Add(ModelCallOf(0, Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 10 })), new long[] { 1 }, 100));
        run.ServiceCalls.Add(ModelCallOf(1, Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 20 })), new long[] { 1 }, 200));
        run.ServiceCalls.Add(ModelCallOf(2, Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 30 })), new long[] { 2 }, 300));
        var replay = RunReplay.Create(run, "/runs/0");

        var (status, scores) = Invoke(replay, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 20, 30 })), 1, 2);
        var (_, changed) = Invoke(replay, Call(2, NamedInput.FromFloat32("x", new[] { 2 }, new float[] { 40, 30 })), 1, 3);

        Assert.True(status.Succeeded);
        Assert.Equal(new float[] { 200, 300 }, scores);
        Assert.True(float.IsNaN(changed[0]) && float.IsNaN(changed[1]));
        Assert.Equal(new[] { ("inputs_changed", "1"), ("not_recorded", "3") }, replay.Gaps.Select(g => (g.Reason, Assert.Single(g.Examples))));
    }

    /// <summary>
    /// Outputs of every element type replay as recorded, at their type (an int64 beyond 2^53 exactly, a float64 unrounded),
    /// in the recorded call and split by row into another batch; a row the recording lacks gets NaN in a float output and
    /// the type's zero in another, and is a gap.
    /// </summary>
    [Fact]
    public void TypedOutputs_ReplayAsRecorded_ExactlyAndRebatched_AndARowTheRecordingLacks_GetsTheGapValue()
    {
        const long Big = 9007199254740993L;
        var recorded = Call(2, NamedInput.FromInt64("x", new[] { 2 }, new long[] { 1, 2 }));
        var run = Run();
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 0, Node = "scorer", Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(recorded).ToList(),
            Outputs = new Dictionary<string, ReplayOutput>
            {
                ["n"] = ReplayJson.Tensor(TensorValues.Of<long>(new long[] { Big, -Big })),
                ["d"] = ReplayJson.Tensor(TensorValues.Of<double>(new[] { 0.1, 1e300 })),
                ["label"] = ReplayJson.Tensor(TensorValues.Of<string>(new[] { "a", "b" })),
                ["ok"] = ReplayJson.Tensor(TensorValues.Of<bool>(new[] { true, true })),
                ["v"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4 })),
            },
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var exact = new ArrayOutputWriter();
        var exactStatus = replay.InvokeModel(recorded.Reading(Typed), exact, new[] { EntityKey.FromInt64(1), EntityKey.FromInt64(2) });
        var rebatched = new ArrayOutputWriter();
        var rebatchedStatus = replay.InvokeModel(Call(3, NamedInput.FromInt64("x", new[] { 3 }, new long[] { 2, 7, 1 })).Reading(Typed), rebatched,
            new[] { EntityKey.FromInt64(2), EntityKey.FromInt64(7), EntityKey.FromInt64(1) });

        Assert.True(exactStatus.Succeeded && rebatchedStatus.Succeeded);
        Assert.Equal(new long[] { Big, -Big }, (long[])exact.Arrays["n"]);
        Assert.Equal(new[] { 0.1, 1e300 }, (double[])exact.Arrays["d"]);
        Assert.Equal(new[] { "a", "b" }, (string[])exact.Arrays["label"]);
        Assert.Equal(new[] { true, true }, (bool[])exact.Arrays["ok"]);
        Assert.Equal(new[] { 1, 2, 3, 4 }, (int[])exact.Arrays["v"]);
        Assert.Equal(new long[] { -Big, 0, Big }, (long[])rebatched.Arrays["n"]);
        Assert.Equal(1e300, ((double[])rebatched.Arrays["d"])[0]);
        Assert.True(double.IsNaN(((double[])rebatched.Arrays["d"])[1]));
        Assert.Equal(0.1, ((double[])rebatched.Arrays["d"])[2]);
        Assert.Equal(new[] { "b", string.Empty, "a" }, (string[])rebatched.Arrays["label"]);
        Assert.Equal(new[] { true, false, true }, (bool[])rebatched.Arrays["ok"]);
        Assert.Equal(new[] { 3, 4, 0, 0, 1, 2 }, (int[])rebatched.Arrays["v"]); // two per row, as recorded
        var gap = Assert.Single(replay.Gaps);
        Assert.Equal(("model", "not_recorded", "7"), (gap.Kind, gap.Reason, Assert.Single(gap.Examples)));
    }

    /// <summary>
    /// Two nodes call one model, writing its output <c>v</c> to a list&lt;int32,2&gt; and a list&lt;int32,3&gt; column. A
    /// rebatched call of the second takes the width of the rows it matches, not of the first node's call.
    /// </summary>
    [Fact]
    public void ARebatchedCall_TakesItsOutputsWidth_FromTheRowsItMatches_NotFromAnotherNodesCall()
    {
        var narrow = Call(2, NamedInput.FromInt32("x", new[] { 2, 2 }, new[] { 1, 2, 3, 4 }));
        var wide = Call(2, NamedInput.FromInt32("x", new[] { 2, 3 }, new[] { 1, 2, 3, 4, 5, 6 }));
        var run = Run();
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 0, Node = "a", Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(narrow).ToList(),
            Outputs = new Dictionary<string, ReplayOutput> { ["v"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4 })) },
        });
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 1, Node = "b", Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(wide).ToList(),
            Outputs = new Dictionary<string, ReplayOutput> { ["v"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4, 5, 6 })) },
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var writer = new ArrayOutputWriter();
        var status = replay.InvokeModel(Call(1, NamedInput.FromInt32("x", new[] { 1, 3 }, new[] { 4, 5, 6 })).Reading("v"), writer, new[] { EntityKey.FromInt64(2) });

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 4, 5, 6 }, (int[])writer.Arrays["v"]);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// The same two nodes; the second's rebatched call matches no recorded row (its inputs changed). Its output takes the
    /// width of that node's recorded calls, not of the first node's, so the row is a gap, not a call of the wrong width.
    /// </summary>
    [Fact]
    public void ARebatchedCall_WhoseRowsAllChanged_TakesItsOutputsWidth_FromItsOwnNodesCalls()
    {
        var narrow = Call(2, NamedInput.FromInt32("x", new[] { 2, 2 }, new[] { 1, 2, 3, 4 }));
        var wide = Call(2, NamedInput.FromInt32("x", new[] { 2, 3 }, new[] { 1, 2, 3, 4, 5, 6 }));
        var run = Run();
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 0, Node = "a", Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(narrow).ToList(),
            Outputs = new Dictionary<string, ReplayOutput> { ["v"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4 })) },
        });
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 1, Node = "b", Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(wide).ToList(),
            Outputs = new Dictionary<string, ReplayOutput> { ["v"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4, 5, 6 })) },
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var writer = new ArrayOutputWriter();
        var status = replay.InvokeModel(Call(1, NamedInput.FromInt32("x", new[] { 1, 3 }, new[] { 4, 5, 7 })).Reading("v"), writer, new[] { EntityKey.FromInt64(2) }, "b");

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 0, 0, 0 }, (int[])writer.Arrays["v"]);
        Assert.Equal(("inputs_changed", "2"), (Assert.Single(replay.Gaps).Reason, Assert.Single(Assert.Single(replay.Gaps).Examples)));
    }

    /// <summary>
    /// A call whose client wrote more values of an output than its rows hold succeeded, and its node failed on them. Its
    /// rows are not split out of that output: a rebatched call that took them would succeed, where the recording failed.
    /// Its other outputs are: one of whole rows, and one the operator does not read (a call-level loss) is left out.
    /// </summary>
    [Fact]
    public void ARebatchedCall_DoesNotSplitRows_OutOfAnOutputLongerThanItsShape()
    {
        var recorded = Call(2, NamedInput.FromInt32("x", new[] { 2, 2 }, new[] { 1, 2, 3, 4 }));
        var run = Run();
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 0, Node = "a", Service = "m", Role = "model", BatchSize = 2, Status = "ok", Keys = Json("[1, 2]"), InputDigests = RowDigests(recorded).ToList(),
            Outputs = new Dictionary<string, ReplayOutput>
            {
                ["v"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4, 5 })),
                ["w"] = ReplayJson.Tensor(TensorValues.Of<int>(new[] { 1, 2, 3, 4 })),
                ["loss"] = ReplayJson.Tensor(TensorValues.Of<float>(new[] { 0.5f })),
            },
        });
        var replay = RunReplay.Create(run, "/runs/0");

        var writer = new ArrayOutputWriter();
        var status = replay.InvokeModel(Call(1, NamedInput.FromInt32("x", new[] { 1, 2 }, new[] { 3, 4 })).Reading("v", "w"), writer, new[] { EntityKey.FromInt64(2) }, "a");

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(new[] { 3, 4 }, (int[])writer.Arrays["w"]);
        Assert.Equal(["w"], writer.Arrays.Keys); // v is absent: a ModelScorer reading it fails, as it failed on the long one
        Assert.Empty(replay.Gaps); // v was recorded, malformed: not a gap
    }

    /// <summary>A float64 input is in the digest as itself: 0.1 and the float nearest it are other inputs.</summary>
    [Fact]
    public void AFloat64InputThatChangedBeyondFloatPrecision_IsAnInputsChangedGap()
    {
        var run = Run();
        run.ServiceCalls.Add(ModelCallOf(0, Call(1, NamedInput.FromFloat64("x", new[] { 1 }, new[] { 0.1 })), new long[] { 1 }, 100));
        var replay = RunReplay.Create(run, "/runs/0");

        var (same, scores) = Invoke(replay, Call(1, NamedInput.FromFloat64("x", new[] { 1 }, new[] { 0.1 })), 1);
        var (_, changed) = Invoke(replay, Call(2, NamedInput.FromFloat64("x", new[] { 2 }, new[] { (double)0.1f, 0.1 })), 1, 1);

        Assert.True(same.Succeeded);
        Assert.Equal(new float[] { 100 }, scores);
        Assert.True(float.IsNaN(changed[0]));
        Assert.Equal(100, changed[1]);
        Assert.Equal(("inputs_changed", "1"), (Assert.Single(replay.Gaps).Reason, Assert.Single(Assert.Single(replay.Gaps).Examples)));
    }

    /// <summary>
    /// Entity 1 was annotated alone twice, with two prompts. The replay's call with the second prompt first takes the call
    /// recorded with the first (same entity, other inputs) and is answered from the one recorded with its own inputs, as
    /// that call was: its text and its generation details.
    /// </summary>
    [Fact]
    public void ASingleRowTextCall_IsAnsweredAsTheRecordedCallOfThatEntityAloneWithTheSameInputs()
    {
        var run = Run();
        run.ServiceCalls.Add(TextCallOf(0, TextCall("a"), new long[] { 1 }, "A"));
        run.ServiceCalls.Add(TextCallOf(1, TextCall("b"), new long[] { 1 }, "B", outputTokens: 3, truncated: true));
        var replay = RunReplay.Create(run, "/runs/0");

        var writer = new ArrayOutputWriter();
        var status = replay.InvokeModel(TextCall("b"), writer, new[] { EntityKey.FromInt64(1) });

        Assert.True(status.Succeeded);
        Assert.Equal((3, true), (status.OutputTokens, status.Truncated));
        Assert.Equal("B", Assert.Single(writer.Texts).Value);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>
    /// Review 30: two annotators share a model; the first one's call for entity 50 failed, the second one's succeeded. When
    /// the first no longer calls 50 in the replay, the second's call must get its own recorded answer, not the failure left
    /// by the other prompt. A recorded failure still answers the call it was (same entity, same inputs).
    /// </summary>
    [Fact]
    public void ARecordedFailure_AnswersOnlyTheCallWithItsInputs_NotAnotherCallOfTheSameEntity()
    {
        var run = Run();
        run.ServiceCalls.Add(FailedTextCallOf(0, TextCall("note 50"), new long[] { 50 }, "note-service-overloaded"));
        run.ServiceCalls.Add(TextCallOf(1, TextCall("why 50"), new long[] { 50 }, "WHY 50"));
        run.ServiceCalls.Add(FailedTextCallOf(2, TextCall("note 60"), new long[] { 60 }, "note-service-overloaded"));
        var replay = RunReplay.Create(run, "/runs/0");

        var why = new ArrayOutputWriter();
        var answered = replay.InvokeModel(TextCall("why 50"), why, new[] { EntityKey.FromInt64(50) }, "annotator");
        var note = replay.InvokeModel(TextCall("note 60"), new ArrayOutputWriter(), new[] { EntityKey.FromInt64(60) }, "notes");

        Assert.True(answered.Succeeded, answered.Error);
        Assert.Equal("WHY 50", Assert.Single(why.Texts).Value);
        Assert.False(note.Succeeded);
        Assert.Equal("note-service-overloaded", note.Error);
        Assert.Empty(replay.Gaps);
    }

    /// <summary>A recorded failure whose inputs changed in the replay is reported as a gap of the entity, not replayed.</summary>
    [Fact]
    public void ARecordedFailure_WhoseInputsChanged_IsAGap_NotTheRecordedFailure()
    {
        var run = Run();
        run.ServiceCalls.Add(FailedTextCallOf(0, TextCall("note 50"), new long[] { 50 }, "note-service-overloaded"));
        var replay = RunReplay.Create(run, "/runs/0");

        var status = replay.InvokeModel(TextCall("note 50, revised"), new ArrayOutputWriter(), new[] { EntityKey.FromInt64(50) });

        Assert.False(status.Succeeded);
        Assert.NotEqual("note-service-overloaded", status.Error);
        Assert.Equal(("not_recorded", "50"), (Assert.Single(replay.Gaps).Reason, Assert.Single(Assert.Single(replay.Gaps).Examples)));
    }

    /// <summary>
    /// A text belongs to its whole call: a two-row call's text is not served to one of its rows, even with the same row
    /// inputs (not recorded); an entity answered alone only with other inputs has changed inputs.
    /// </summary>
    [Fact]
    public void AMultiRowCallsText_IsNotSplitByRow_AndAnEntityAnsweredOnlyWithOtherInputs_HasChangedInputs()
    {
        var run = Run();
        run.ServiceCalls.Add(TextCallOf(0, TextCall("a", batch: 2), new long[] { 1, 2 }, "AB"));
        run.ServiceCalls.Add(TextCallOf(1, TextCall("a"), new long[] { 3 }, "C"));
        var replay = RunReplay.Create(run, "/runs/0");

        var first = new ArrayOutputWriter();
        var split = replay.InvokeModel(TextCall("a"), first, new[] { EntityKey.FromInt64(1) });
        var second = new ArrayOutputWriter();
        var changed = replay.InvokeModel(TextCall("b"), second, new[] { EntityKey.FromInt64(3) });

        Assert.False(split.Succeeded);
        Assert.False(changed.Succeeded);
        Assert.Empty(first.Texts);
        Assert.Empty(second.Texts);
        Assert.Equal(new[] { ("inputs_changed", "3"), ("not_recorded", "1") }, replay.Gaps.Select(g => (g.Reason, Assert.Single(g.Examples))));
    }

    /// <summary>
    /// A client's own failure, thrown or returned, is a failed status, live and on replay; only a call the node's budget or
    /// the caller cut off throws, as cancellation, so a replay never has to recreate an exception type (it used to, by
    /// reflection over the loaded assemblies, falling back to a stand-in type).
    /// </summary>
    [Theory]
    [InlineData("error", false, "threw InvalidOperationException: backend said no")]
    [InlineData("timeout", false, "threw TimeoutException: backend said no")]
    [InlineData("timeout", true, null)]
    [InlineData("cancelled", true, null)]
    public void ARecordedFailure_IsAFailedStatus_AndACutOffCall_IsCancellation(string status, bool cutOff, string? error)
    {
        var call = Call(1, NamedInput.FromFloat32("x", new[] { 1 }, new float[] { 10 }));
        var recorded = ModelCallOf(0, call, new long[] { 1 });
        var run = Run();
        run.ServiceCalls.Add(new ReplayServiceCall
        {
            Seq = 0, Node = recorded.Node, Service = "m", Role = "model", BatchSize = 1, Status = status, Error = error ?? "the node's budget ran out", Threw = cutOff,
            Keys = recorded.Keys, InputDigests = recorded.InputDigests,
        });
        var replay = RunReplay.Create(run, "/runs/0");

        if (cutOff)
        {
            Assert.Equal("the node's budget ran out", Assert.ThrowsAny<OperationCanceledException>(() => Invoke(replay, call, 1)).Message);
        }
        else
        {
            var replayed = Invoke(replay, call, 1).Status;
            Assert.Equal((false, error), (replayed.Succeeded, replayed.Error));
        }
    }

    /// <summary>A call of model <c>m</c> that reads its output <c>score</c>, as a model operator names what it reads.</summary>
    internal static ServiceCall Call(int batch, params NamedInput[] inputs) => new() { Service = "m", Inputs = inputs, BatchSize = batch, Outputs = Score };

    private static readonly string[] Score = ["score"];

    private static readonly string[] Answer = ["answer"];

    private static readonly string[] Typed = ["n", "d", "label", "ok", "v"];

    internal static string[] RowDigests(ServiceCall call)
    {
        var order = ServiceInputDigest.NameOrder(call);
        return Enumerable.Range(0, call.BatchSize).Select(r => ReplayJson.Hex(ServiceInputDigest.Row(call, order, r))).ToArray();
    }

    /// <summary>A successful recorded call of model <c>m</c> scoring <paramref name="keys"/>, one <c>score</c> per row.</summary>
    internal static ReplayServiceCall ModelCallOf(int seq, ServiceCall call, long[] keys, params float[] scores) => new()
    {
        Seq = seq,
        Node = "ranker",
        Service = "m",
        Role = "model",
        BatchSize = call.BatchSize,
        Status = "ok",
        Keys = Json("[" + string.Join(",", keys) + "]"),
        InputDigests = RowDigests(call).ToList(),
        Outputs = new Dictionary<string, ReplayOutput> { ["score"] = new() { Type = "float32", Values = JsonSerializer.SerializeToElement(scores) } },
    };

    /// <summary>A generative call of model <c>m</c>: one prompt, <c>user</c>, and a token limit.</summary>
    internal static ServiceCall TextCall(string prompt, int batch = 1) => new()
    {
        Service = "m", Inputs = new[] { NamedInput.FromText("user", prompt) }, BatchSize = batch, Options = new ServiceCallOptions(MaxOutputTokens: 64),
        Outputs = Answer,
    };

    /// <summary>A successful recorded generative call of model <c>m</c> for <paramref name="keys"/>, answering <paramref name="text"/>.</summary>
    internal static ReplayServiceCall TextCallOf(int seq, ServiceCall call, long[] keys, string text, int? outputTokens = null, bool? truncated = null) => new()
    {
        Seq = seq,
        Node = "annotator",
        Service = "m",
        Role = "model",
        BatchSize = call.BatchSize,
        Status = "ok",
        Keys = Json("[" + string.Join(",", keys) + "]"),
        InputDigests = RowDigests(call).ToList(),
        Texts = new Dictionary<string, string> { ["answer"] = text },
        OutputTokens = outputTokens,
        Truncated = truncated,
    };

    /// <summary>A recorded generative call of model <c>m</c> for <paramref name="keys"/> that failed with <paramref name="error"/>.</summary>
    internal static ReplayServiceCall FailedTextCallOf(int seq, ServiceCall call, long[] keys, string error) => new()
    {
        Seq = seq,
        Node = "notes",
        Service = "m",
        Role = "model",
        BatchSize = call.BatchSize,
        Status = "error",
        Error = error,
        Keys = Json("[" + string.Join(",", keys) + "]"),
        InputDigests = RowDigests(call).ToList(),
    };

    internal static (ServiceStatus Status, float[] Scores) Invoke(RunReplay replay, ServiceCall call, params long[] keys)
    {
        var writer = new ArrayOutputWriter();
        var status = replay.InvokeModel(call, writer, keys.Select(EntityKey.FromInt64).ToArray());
        return (status, writer.Floats.TryGetValue("score", out var scores) ? scores : Array.Empty<float>());
    }

    internal sealed class ArrayOutputWriter : IServiceResponse
    {
        public void AddItem(long entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public void AddItem(string entityId, double score) => throw new NotSupportedException("a model answers with outputs");

        public Dictionary<string, float[]> Floats { get; } = new();

        public Dictionary<string, string> Texts { get; } = new();

        /// <summary>Every tensor output as written, of any element type.</summary>
        public Dictionary<string, Array> Arrays { get; } = new();

        public Memory<T> GetValues<T>(string name, int elementCount)
        {
            var buffer = new T[elementCount];
            Arrays[name] = buffer;
            if (buffer is float[] floats)
            {
                Floats[name] = floats;
            }

            return buffer;
        }

        public void SetText(string name, string text) => Texts[name] = text;
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
            Seq = seq, Source = "s", Entity = "item", Keys = Json(keys), Status = "ok", Fields = bindings, Values = values,
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
            KeyFeatures = [],
            FeatureIndices = [.. new int[fields.Length]],
            Fields = [.. bindings],
        };
        var staging = new StagingBuffer(bindings, keys.Length, new RequestMemory());
        var batch = new EntityKeyBatch(keys.Select(EntityKey.FromInt64).ToArray());
        return (replay.Replay(group, batch, [.. bindings], staging), staging);
    }

    internal static double?[] Values(StagingBuffer staging, int field, int keys) =>
        Enumerable.Range(0, keys).Select(k => staging.Get(field, k) is { IsMissing: false } v ? v.AsFloat64() : (double?)null).ToArray();
}

/// <summary>A model call answered by a replay as an operator would send it: its rows' entities as its items, read back into a writer.</summary>
internal static class ReplayModelCalls
{
    public static ServiceStatus InvokeModel(this RunReplay replay, ServiceCall call, RunReplayTests.ArrayOutputWriter writer, EntityKey[]? keys, string node = "ranker")
    {
        var keyed = new ServiceCall
        {
            Service = call.Service,
            Version = call.Version,
            Inputs = call.Inputs,
            BatchSize = call.BatchSize,
            Items = keys ?? [],
            Outputs = call.Outputs,
            Options = call.Options,
        };
        var response = new ServiceResponse(new RequestMemory());
        var status = replay.Call(node, new IoTarget(IoRoles.Model, call.Service), keyed, response, out _);
        foreach (var (name, values) in response.Tensors)
        {
            switch (values.ElementType)
            {
                case TensorElementType.Bool: values.Bool.CopyTo(writer.GetValues<bool>(name, values.Length)); break;
                case TensorElementType.Int32: values.Int32.CopyTo(writer.GetValues<int>(name, values.Length)); break;
                case TensorElementType.Int64: values.Int64.CopyTo(writer.GetValues<long>(name, values.Length)); break;
                case TensorElementType.Float32: values.Float32.CopyTo(writer.GetValues<float>(name, values.Length)); break;
                case TensorElementType.Float64: values.Float64.CopyTo(writer.GetValues<double>(name, values.Length)); break;
                default: values.String.CopyTo(writer.GetValues<string>(name, values.Length)); break;
            }
        }

        foreach (var (name, text) in response.Texts)
        {
            writer.SetText(name, text);
        }

        return status;
    }
}

/// <summary>The same call, reading other outputs.</summary>
internal static class ServiceCallReading
{
    public static ServiceCall Reading(this ServiceCall call, params string[] outputs) => new()
    {
        Service = call.Service, Version = call.Version, Inputs = call.Inputs, BatchSize = call.BatchSize, Items = call.Items, Outputs = outputs,
        Limit = call.Limit, Options = call.Options,
    };
}
