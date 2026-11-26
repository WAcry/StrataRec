using System.Text.Json.Nodes;
using StrataRec.Artifacts.Model;
using StrataRec.Hosting.Replay;
using StrataRec.Pipeline.Replay;

namespace StrataRec.UnitTests.Hosting;

/// <summary>Malformed replay bundles are rejected with the JSON pointer of the problem and a hint.</summary>
public sealed class ReplayBundleFileTests
{
    private const string Minimal = """
        {
          "format": "stratarec.replay_bundle/2026-11-26.1",
          "request": { "pipeline_id": "p", "request_id": "r", "seed": 1, "time_ms": 0, "deadline_ms": 100, "assignment": { "source": "none" } },
          "manifest": { "id": "m" },
          "runs": [
            {
              "pipeline_id": "p", "manifest_id": "m", "role": "primary",
              "feature_calls": [
                { "seq": 0, "source": "item_store", "entity": "item", "keys": [1, 2], "status": "ok",
                  "fields": [{ "name": "score", "type": "float64" }], "values": { "score": [0.5, null] } }
              ],
              "sampling": { "logs": "none" },
              "result": { "succeeded": true }
            }
          ],
          "result": { "served_by": "p", "succeeded": true, "column_types": { "score": "float64" }, "items": [{ "id": 1, "rank": 1, "columns": { "score": 0.5 } }] }
        }
        """;

    [Fact]
    public void ABundleOfThePreviousFormat_IsRejectedAtTheFormat()
    {
        // 2026-11-26.1 numbers a node's pipeline calls over its run and records how many each attempt made; 2026-11-25.1
        // records the calls of other pipelines; 2026-11-22.1 records the result's request-level values;
        // 2026-11-21.15 adds what an item saw of an input of more than sixteen numbers or bools, or of strings, to its digest
        // as a digest of its own; 2026-11-21.14 of more than four, or of strings; 2026-11-21.13 of more than sixteen values;
        // 2026-11-21.12 added every one so;
        // 2026-11-21.11 digests an item by what it saw of each input;
        // 2026-11-21.10 records the entity of a source's call;
        // 2026-11-21.9 records the outputs each call asked for, and a source's call carries the request's seed;
        // 2026-11-21.8 records a feature source's service call with its source (IO step 5);
        // 2026-11-21.7 digests every call without its limit again and records the items a service dropped past the limit;
        // 2026-11-21.6 digests an output-reading call with its limit and a per-item missing input by its rows, and records a
        // call's degraded reason; .5 sent a recall query of -0.0 or a declared float32 otherwise, so it could replay identical
        // to a changed run (review of IO step 3).
        // 2026-11-21.5 is .4's layout where a service sees only its call's inputs (in .4 a recall could read a request field no
        // digest covers); 2026-11-21.4 records model and recall calls as service calls, by the digest of their inputs; 2026-11-21.3 records
        // request inputs at the host's types, which 2026-11-21.2 (laid out alike) recorded at the primary's; .2 replays a float32 request input as the nearest float, which .1 replayed as the double given;
        // 2026-11-20.3 has float32 model outputs only, as bare arrays. There are no old readers.
        foreach (var previous in new[] { "2026-11-25.1", "2026-11-22.1", "2026-11-21.15", "2026-11-21.14", "2026-11-21.13", "2026-11-21.12", "2026-11-21.11", "2026-11-21.10", "2026-11-21.9", "2026-11-21.8", "2026-11-21.7", "2026-11-21.6", "2026-11-21.5", "2026-11-21.4", "2026-11-21.3", "2026-11-21.2", "2026-11-21.1", "2026-11-20.3" })
        {
            var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Minimal.Replace("2026-11-26.1", previous, StringComparison.Ordinal)));

            Assert.Equal("/format", error.Pointer);
        }

        Assert.Equal("stratarec.replay_bundle/2026-11-26.1", ReplayBundle.CurrentFormat);
    }

    [Fact]
    public void MinimalBundle_Parses_AndItsRunIndexes()
    {
        var bundle = ReplayBundleFile.Parse(Minimal);

        Assert.Equal(ReplayBundle.CurrentFormat, bundle.Format);
        var replay = RunReplay.Create(bundle.Runs[0], "/runs/0");
        Assert.Empty(replay.Gaps);
        Assert.Single(ComparableResult.From(bundle.Result, "/result").Ids);
    }

    [Fact]
    public void NonFiniteCandidateScores_RoundTripAsTheirNames()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["service_calls"] = JsonNode.Parse(
            """[{ "seq": 1, "node": "recall", "service": "s", "role": "recall", "limit": 3, "status": "ok", "ids": [1, 2, 3], "scores": ["NaN", "Infinity", 0.5] }]""")));
        bundle.Runs[0].ServiceCalls[0].Scores.Add(double.NegativeInfinity);

        var json = ReplayBundleFile.Serialize(bundle);

        Assert.Contains("\"-Infinity\"", json);
        Assert.Equal(new[] { double.NaN, double.PositiveInfinity, 0.5, double.NegativeInfinity }, ReplayBundleFile.Parse(json).Runs[0].ServiceCalls[0].Scores);
    }

    [Theory]
    [InlineData("/request")]
    [InlineData("/request/pipeline_id")]
    [InlineData("/request/assignment/arms")]
    [InlineData("/runs")]
    [InlineData("/runs/0")]
    [InlineData("/runs/0/nodes")]
    [InlineData("/runs/0/feature_calls/0/fields")]
    [InlineData("/runs/0/feature_calls/0/fields/0")]
    [InlineData("/runs/0/feature_calls/0/fields/0/name")]
    [InlineData("/result/items/0")]
    [InlineData("/result/column_types/score")]
    public void ANullWhereAValueIsRequired_IsRejectedAtItsPointer(string pointer)
    {
        var json = Edit(b =>
        {
            b["request"]!["assignment"]!["arms"] = new JsonArray("e/a");
            b["runs"]![0]!["nodes"] = new JsonArray(JsonNode.Parse("""{ "node": "n", "outcome": "succeeded" }"""));
            var parent = Locate(b, pointer[..pointer.LastIndexOf('/')]);
            var token = pointer[(pointer.LastIndexOf('/') + 1)..];
            if (parent is JsonArray array)
            {
                array[int.Parse(token)] = null;
            }
            else
            {
                parent[token] = null;
            }
        });

        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(json));

        Assert.Equal(pointer, error.Pointer);
        Assert.Matches("must not be null|got null", error.Reason); // a member: the binder; an element of a list or map: the walk
        Assert.Contains("stratarec schema replay_bundle", error.Hint);
    }

    [Fact]
    public void NullsWhereTheSchemaAllowsThem_AreAccepted()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b =>
        {
            b["result"]!["error"] = null;
            b["result"]!["items"]![0]!["columns"]!["score"] = null; // a missing value
            b["request"]!["inputs"] = JsonNode.Parse("""{ "user_id": { "type": "int64?", "value": null } }""");
            b["runs"]![0]!["feature_calls"]![0]!["error"] = null;
        }));

        Assert.True(ComparableResult.From(bundle.Result, "/result").Columns[0]["score"].IsMissing);
    }

    private static JsonNode Locate(JsonNode root, string pointer)
    {
        var node = root;
        foreach (var token in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonArray array ? array[int.Parse(token)]! : node[token]!;
        }

        return node;
    }

    [Fact]
    public void OtherFormat_IsRejectedAtFormat()
    {
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Edit(b => b["format"] = "stratarec.replay_bundle/2099-01-01.1")));

        Assert.Equal("/format", error.Pointer);
        Assert.Contains("record the request again", error.Hint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF")] // a text that kept its file's byte order mark (the version was not read, the layout was)
    public void AnotherFormat_IsRejectedAtFormat_BeforeItsLayoutIsReadAsThisOne(string prefix)
    {
        // Another format's layout: read as this one it would be a structural error somewhere inside.
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(prefix + Edit(b =>
        {
            b["format"] = "stratarec.replay_bundle/2026-01-01.1";
            b["request"]!["assignment"] = JsonNode.Parse("""{ "source": "builtin", "arms": [], "unknown_arms": 1 }""");
        })));

        Assert.Equal("/format", error.Pointer);
        Assert.Contains("stratarec.replay_bundle/2026-01-01.1", error.Message);
        Assert.Contains("record the request again", error.Hint);
    }

    [Fact]
    public void IgnoredArms_RoundTrip_WithTheirReasons()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["request"]!["assignment"] = JsonNode.Parse("""
            { "source": "external", "arms": ["e/a"], "ignored": [{ "id": "f/x", "reason": "layer_conflict" }, { "id": "g/y", "reason": "unknown" }] }
            """)));

        var ignored = bundle.Request.Assignment.Ignored;
        Assert.Equal(new[] { ("f/x", "layer_conflict"), ("g/y", "unknown") }, ignored.Select(i => (i.Id, i.Reason)));
        Assert.Contains("\"layer_conflict\"", ReplayBundleFile.Serialize(bundle));
    }

    [Theory]
    [InlineData("seed")] // a number field: describing the string at fault must not decode it
    [InlineData("format")] // read before the structure, to check the version
    public void AStringThatIsNotValidUnicode_IsRejectedAsMalformed_NotThrownAsAnotherException(string member)
    {
        // "\uD800" (an unpaired surrogate escape) escaped as InvalidOperationException.
        var unpaired = "\"" + (char)92 + "uD800\"";
        var json = member == "seed"
            ? """{"format":"stratarec.replay_bundle/2026-11-26.1","request":{"seed":""" + unpaired + "}}"
            : """{"format":""" + unpaired + ""","request":{}}""";

        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(json));

        Assert.Contains("not valid Unicode", error.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void UnknownProperty_IsRejectedWithItsPointer_AndPointsToTheSchema()
    {
        var error = Assert.Throws<ReplayBundleException>(() => ReplayBundleFile.Parse(Edit(b => b["request"]!["sede"] = 1)));

        Assert.Equal("/request/sede", error.Pointer);
        Assert.Contains("Unknown key 'sede'", error.Message);
        Assert.Contains("did you mean 'seed'?", error.Hint);
    }

    [Fact]
    public void ValueThatDoesNotFitItsRecordedType_IsRejectedAtTheValue()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["feature_calls"]![0]!["values"]!["score"]![1] = "high"));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(bundle.Runs[0], "/runs/0"));

        Assert.Equal("/runs/0/feature_calls/0/values/score/1", error.Pointer);
    }

    [Fact]
    public void ValuesOfTheWrongLength_AreRejectedAtTheField()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["feature_calls"]![0]!["values"]!["score"] = new JsonArray(0.5)));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(bundle.Runs[0], "/runs/0"));

        Assert.Equal("/runs/0/feature_calls/0/values/score", error.Pointer);
    }

    [Fact]
    public void UnknownStatus_IsRejectedWithTheAcceptedSpellings()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["runs"]![0]!["feature_calls"]![0]!["status"] = "fine"));

        var error = Assert.Throws<ReplayBundleException>(() => RunReplay.Create(bundle.Runs[0], "/runs/0"));

        Assert.Equal("/runs/0/feature_calls/0/status", error.Pointer);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void ResultColumnWithoutAType_IsRejectedAtTheColumn()
    {
        var bundle = ReplayBundleFile.Parse(Edit(b => b["result"]!["column_types"] = new JsonObject()));

        var error = Assert.Throws<ReplayBundleException>(() => ComparableResult.From(bundle.Result, "/result"));

        Assert.Equal("/result/items/0/columns/score", error.Pointer);
    }

    private static string Edit(Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(Minimal)!;
        edit(node);
        return node.ToJsonString();
    }
}
