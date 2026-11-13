using System.Globalization;
using System.Text;
using StrataRec.Core.Columns;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// The request read path of local feature sources (design 6.6): the same values, defaults, missing values and states as
/// a fetch of the same records, one pinned table version per request, and record/replay through the call hook.
/// </summary>
public class LocalFeatureReadTests
{
    private static readonly EntityDefinition Author = FeatureTestRig.ForeignKey("author", "item.author_id");

    /// <summary>Items 1–5 and 7 (6 has no record); item 2 has no age, item 4 no author, item 5's author 77 has no record.</summary>
    private static readonly Dictionary<long, Dictionary<string, object>> ItemRecords = new()
    {
        [1] = new() { ["clicks"] = 3.0, ["age"] = 1.5, ["category"] = "music", ["emb"] = new[] { 1f, 2f, 3f, 4f }, ["author_id"] = 10L },
        [2] = new() { ["clicks"] = 8.0, ["category"] = "food", ["author_id"] = 11L },
        [3] = new() { ["age"] = 9.0, ["emb"] = new[] { 0f, 0f, 1f, 0f }, ["author_id"] = 10L },
        [4] = new() { ["clicks"] = 1.0, ["age"] = 2.0, ["category"] = "news" },
        [5] = new() { ["clicks"] = 2.0, ["author_id"] = 77L },
        [7] = new() { ["clicks"] = 4.0, ["author_id"] = 11L },
    };

    private static readonly Dictionary<long, Dictionary<string, object>> AuthorRecords = new()
    {
        [10] = new() { ["followers"] = 1000.0 },
        [11] = new() { ["followers"] = 25.0, ["verified"] = 1L },
    };

    private static readonly Dictionary<long, Dictionary<string, object>> UserRecords = new()
    {
        [1001] = new() { ["ctr"] = 0.05 },
        [1002] = new(),
    };

    [Theory]
    [InlineData(1001L)]
    [InlineData(1002L)]
    [InlineData(9999L)]
    [InlineData(null)]
    public async Task LocalRead_GivesTheValuesAndStatesOfAFetchOfTheSameRecords(long? userId)
    {
        var candidates = new long[] { 7, 1, 2, 3, 4, 5, 6 };
        var (local, localFeatures) = SampleRig(fetched: false);
        var (fetched, fetchedFeatures) = SampleRig(fetched: true);
        var localRequest = local.NewRequest(candidates, userId);
        var fetchedRequest = fetched.NewRequest(candidates, userId);

        localRequest.Fetcher.StartContextFetch(localFeatures, CancellationToken.None);
        fetchedRequest.Fetcher.StartContextFetch(fetchedFeatures, CancellationToken.None);
        var localResult = await localRequest.EnsureAsync(localFeatures);
        var fetchedResult = await fetchedRequest.EnsureAsync(fetchedFeatures);

        Assert.Equal(fetchedResult, localResult);
        Assert.Equal(FeatureState.Ok, localResult.State);
        foreach (var feature in localFeatures)
        {
            var info = local.Plan.Features[feature];
            Assert.Equal(fetchedRequest.Fetcher.GetState(feature), localRequest.Fetcher.GetState(feature));
            if (info.IsContext)
            {
                Assert.Equal(fetchedRequest.ContextValue(feature), localRequest.ContextValue(feature));
                continue;
            }

            for (var row = 0; row < candidates.Length; row++)
            {
                var expected = fetchedRequest.Value(feature, row);
                Assert.True(expected.Equals(localRequest.Value(feature, row)),
                    $"{info.Definition.Name} of item {candidates[row]}: fetched {expected}, local {localRequest.Value(feature, row)}");
            }
        }

        // Spot checks of the shared semantics: default for a key without a record, missing for a nullable feature, absent
        // for a missing foreign key or one without a record.
        Assert.Equal(0.0, localRequest.Value(localFeatures[0], 6).AsFloat64()); // item 6: clicks default
        Assert.True(localRequest.Value(localFeatures[1], 2).IsMissing); // item 2: no age
        Assert.Equal("unknown", localRequest.Value(localFeatures[2], 3).AsString()); // item 3: category default
        Assert.Equal(-1.0, localRequest.Value(localFeatures[5], 4).AsFloat64()); // item 4: no author_id
        Assert.Equal(-1.0, localRequest.Value(localFeatures[5], 5).AsFloat64()); // item 5: author 77 has no record
        Assert.Equal(1000.0, localRequest.Value(localFeatures[5], 1).AsFloat64());
        var ctr = localRequest.ContextValue(localFeatures[7]);
        Assert.Equal(userId == 1001 ? 0.05 : 0.02, ctr.AsFloat64(), 6);
    }

    [Fact]
    public async Task NonFiniteValues_AreAbsent_AndCounted()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            Write(writer, 1, new() { ["score"] = double.NaN, ["weight"] = double.PositiveInfinity });
            Write(writer, 2, new() { ["score"] = 2.5, ["weight"] = double.NegativeInfinity });
        });
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store", ScalarValue.FromFloat64(-1));
        var weight = rig.Physical("item.weight", FeatureTestRig.Item, ColumnType.Float32, "item_store");
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2 });

        var result = await request.EnsureAsync(score, weight);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(-1.0, request.Value(score, 0).AsFloat64());
        Assert.Equal(2.5, request.Value(score, 1).AsFloat64());
        Assert.True(request.Value(weight, 0).IsMissing);
        Assert.True(request.Value(weight, 1).IsMissing);
        Assert.Equal(3, request.Fetcher.NonFiniteReplaced);
    }

    [Fact]
    public async Task ATableThatIsNotLoaded_FailsTheFeatures_WithAnErrorNamingTheTable()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", load: null).LocalSource("user_store", load: null);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0));
        var ctr = rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float32, "user_store", ScalarValue.FromFloat64(0));
        rig.Build();
        var request = rig.NewRequest(new long[] { 1 }, userId: 1001);

        request.Fetcher.StartContextFetch(new[] { ctr }, CancellationToken.None);
        var candidate = await request.EnsureAsync(clicks);
        var context = await request.EnsureAsync(ctr);

        Assert.True(candidate.Failed);
        Assert.Contains("local table item_store/item (source 'item_store' in bindings:test) is not loaded", candidate.Error);
        Assert.True(context.Failed);
        Assert.Contains("user_store/user", context.Error);
        Assert.False(request.IsFilled(clicks, 0)); // never served as missing
    }

    [Theory]
    [InlineData(FetchOutcome.Failed, FeatureState.Failed)]
    [InlineData(FetchOutcome.Partial, FeatureState.Degraded)]
    public async Task AFailedOrDegradedForeignKey_FailsOrDegradesTheLocalFeaturesItKeys(FetchOutcome keyOutcome, FeatureState expected)
    {
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => 10L);
            return ValueTask.FromResult(new FetchStatus(keyOutcome, 1, "item_store is having a bad day"));
        });
        var rig = new FeatureTestRig()
            .Source("item_store", connector)
            .LocalSource("author_store", writer => Write(writer, 10, new() { ["followers"] = 5.0 }));
        rig.Physical("item.author_id", FeatureTestRig.Item, ColumnType.Int64, "item_store");
        var followers = rig.Physical("author.followers", Author, ColumnType.Float64, "author_store", ScalarValue.FromFloat64(0));
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2 });

        var result = await request.EnsureAsync(followers);

        Assert.Equal(expected, result.State);
        Assert.Equal(expected, request.Fetcher.GetState(followers));
        if (expected == FeatureState.Degraded)
        {
            Assert.Equal(5.0, request.Value(followers, 0).AsFloat64());
        }
    }

    [Fact]
    public async Task ARequest_ReadsTheVersionItPinned_WhenAReloadSwapsANewOneInMidRequest()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", writer => Write(writer, 1, new() { ["clicks"] = 1.0, ["age"] = 10.0 }));
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var first = rig.NewRequest(new long[] { 1 });
        await first.EnsureAsync(clicks);

        var writer = rig.Tables["item_store"].Writer;
        writer.BeginReload();
        Write(writer, 1, new() { ["clicks"] = 2.0, ["age"] = 20.0 }, version: 2);
        writer.CompleteReload(UpdatePosition.Empty);
        await first.EnsureAsync(age);
        var second = rig.NewRequest(new long[] { 1 });
        await second.EnsureAsync(clicks, age);

        Assert.Equal((1.0, 10.0), (first.Value(clicks, 0).AsFloat64(), first.Value(age, 0).AsFloat64()));
        Assert.Equal((2.0, 20.0), (second.Value(clicks, 0).AsFloat64(), second.Value(age, 0).AsFloat64()));
    }

    [Fact]
    public async Task RequestsRunningWhileReloadsSwapVersions_EachReadOneCompleteVersion()
    {
        // A dedicated thread reloads the table a fixed number of times, every record of reload r holding r in both
        // fields; requests on this thread read the fields in two separate reads. Whatever the interleaving, a request
        // sees one reload, whole: every row and both reads the same r.
        const int Items = 256;
        const int Reloads = 200;
        var rig = new FeatureTestRig().LocalSource("item_store", writer => LoadGeneration(writer, Items, 0));
        var a = rig.Physical("item.a", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var b = rig.Physical("item.b", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var writer = rig.Tables["item_store"].Writer;
        var reloaded = 0;
        var thread = new Thread(() =>
        {
            for (var r = 1; r <= Reloads; r++)
            {
                writer.BeginReload();
                LoadGeneration(writer, Items, r);
                writer.CompleteReload(UpdatePosition.Empty);
                Volatile.Write(ref reloaded, r);
            }
        }) { IsBackground = true, Name = "local table reloads" };
        var candidates = Enumerable.Range(1, Items).Select(i => (long)i).ToArray();
        var seen = new HashSet<double>();

        thread.Start();
        do
        {
            var request = rig.NewRequest(candidates);
            Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(a)).State);
            Assert.Equal(FeatureState.Ok, (await request.EnsureAsync(b)).State);
            var generation = request.Value(a, 0).AsFloat64();
            for (var row = 0; row < Items; row++)
            {
                Assert.Equal(generation, request.Value(a, row).AsFloat64());
                Assert.Equal(generation, request.Value(b, row).AsFloat64());
            }

            seen.Add(generation);
            request.Memory.Release();
        }
        while (Volatile.Read(ref reloaded) < Reloads);

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task RowsAppendedAfterARead_AreFilledByTheNextRead_AndEarlierRowsKeepTheirValues()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            for (var id = 1; id <= 4; id++)
            {
                Write(writer, id, new() { ["clicks"] = id * 1.0, ["age"] = id * 10.0 });
            }
        });
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        rig.Build();
        var request = rig.NewRequest(new long[] { 1, 2 }, capacity: 4);
        await request.EnsureAsync(clicks);
        ColumnValues.Set(request.Table.TryGetSlot(rig.SlotOf(clicks))!, 0, ScalarValue.FromFloat64(-5)); // a node's own write

        request.Table.AppendRows(new long[] { 3, 4 });
        var result = await request.EnsureAsync(clicks, age);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(new[] { -5.0, 2, 3, 4 }, Enumerable.Range(0, 4).Select(r => request.Value(clicks, r).AsFloat64()));
        Assert.Equal(new[] { 10.0, 20, 30, 40 }, Enumerable.Range(0, 4).Select(r => request.Value(age, r).AsFloat64()));
    }

    [Fact]
    public async Task ARecordingRequest_RecordsEachLocalRead_AsOneCallWithItsDistinctKeysAndValues()
    {
        var rig = new FeatureTestRig()
            .MemorySource("item_store", """{ "1": { "author_id": 11 }, "2": { "author_id": 10 }, "3": { "author_id": 11 }, "4": {} }""")
            .LocalSource("author_store", writer => Write(writer, 11, new() { ["followers"] = 25.0 }));
        rig.Physical("item.author_id", FeatureTestRig.Item, ColumnType.Int64, "item_store");
        var followers = rig.Physical("author.followers", Author, ColumnType.Float64, "author_store", ScalarValue.FromFloat64(0));
        rig.Build();
        var hook = new FeatureCallHookTests.ListHook();
        var request = rig.NewRequest(new long[] { 1, 2, 3, 4 }, hook: hook);

        await request.EnsureAsync(followers);

        var call = Assert.Single(hook.Recorded, c => c.Source == "author_store");
        Assert.Equal(new long[] { 11, 10 }, call.Keys); // live-row order, once each; item 4 has no author
        Assert.Equal(new double?[] { 25.0, null }, call.Values); // as stored: no record is missing, not the default
        Assert.Equal((true, IoOutcome.Ok, FetchOutcome.Ok), (call.Sent, call.Outcome, call.Status.Outcome));
    }

    [Fact]
    public async Task AReplayingRequest_AnswersLocalReadsFromTheRecording_AndNeverReadsTheTable()
    {
        var rig = new FeatureTestRig().LocalSource("item_store", load: null).LocalSource("user_store", load: null);
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var ctr = rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float64, "user_store");
        rig.Build();
        var request = rig.NewRequest(new long[] { 4, 5 }, userId: 3, hook: new FeatureCallHookTests.ListHook(replays: true));

        request.Fetcher.StartContextFetch(new[] { ctr }, CancellationToken.None);
        var candidate = await request.EnsureAsync(score);
        var context = await request.EnsureAsync(ctr);

        Assert.False(candidate.Failed, candidate.Error); // the table was never loaded, and never read
        Assert.False(context.Failed, context.Error);
        Assert.Equal(40.0, request.Value(score, 0).AsFloat64());
        Assert.Equal(50.0, request.Value(score, 1).AsFloat64());
        Assert.Equal(30.0, request.ContextValue(ctr).AsFloat64());
    }

    /// <summary>
    /// Items, authors (through <c>item.author_id</c>) and users, bound either to local tables or to <c>memory@1</c> with
    /// the same records. Features: clicks (default 0), age (nullable), category (default "unknown"), embedding (nullable),
    /// author_id, author followers (default -1), author verified (nullable), user ctr (default 0.02).
    /// </summary>
    private static (FeatureTestRig Rig, int[] Features) SampleRig(bool fetched)
    {
        var rig = new FeatureTestRig();
        foreach (var (source, records) in new[] { ("item_store", ItemRecords), ("author_store", AuthorRecords), ("user_store", UserRecords) })
        {
            if (fetched)
            {
                rig.MemorySource(source, MemoryJson(records));
            }
            else
            {
                rig.LocalSource(source, writer =>
                {
                    foreach (var (key, fields) in records)
                    {
                        Write(writer, key, fields);
                    }
                });
            }
        }

        var features = new[]
        {
            rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float32, "item_store", ScalarValue.FromFloat64(0)),
            rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float32, "item_store"),
            rig.Physical("item.category", FeatureTestRig.Item, ColumnType.String, "item_store", ScalarValue.FromString("unknown")),
            rig.Physical("item.emb", FeatureTestRig.Item, ColumnType.Vector(4), "item_store"),
            rig.Physical("item.author_id", FeatureTestRig.Item, ColumnType.Int64, "item_store"),
            rig.Physical("author.followers", Author, ColumnType.Float64, "author_store", ScalarValue.FromFloat64(-1)),
            rig.Physical("author.verified", Author, ColumnType.Int64, "author_store"),
            rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float32, "user_store", ScalarValue.FromFloat64(0.02)),
        };
        rig.Build();
        return (rig, features);
    }

    private static string MemoryJson(Dictionary<long, Dictionary<string, object>> records)
    {
        var json = new StringBuilder("{");
        foreach (var (key, fields) in records)
        {
            json.Append(json.Length == 1 ? string.Empty : ",").Append(CultureInfo.InvariantCulture, $"\"{key}\": {{");
            var first = true;
            foreach (var (name, value) in fields)
            {
                json.Append(first ? string.Empty : ",").Append('"').Append(name).Append("\": ").Append(value switch
                {
                    string s => "\"" + s + "\"",
                    float[] v => "[" + string.Join(",", v.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]",
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException(value.ToString()),
                });
                first = false;
            }

            json.Append('}');
        }

        return json.Append('}').ToString();
    }

    private static void LoadGeneration(IFeatureTableWriter writer, int items, int generation)
    {
        for (var id = 1; id <= items; id++)
        {
            var builder = writer.Begin(id, 1);
            builder.Set(writer.Schema.FieldIndex("a"), ScalarValue.FromFloat64(generation));
            builder.Set(writer.Schema.FieldIndex("b"), ScalarValue.FromFloat64(generation));
            builder.Commit();
        }
    }

    private static void Write(IFeatureTableWriter writer, long key, Dictionary<string, object> fields, long version = 1)
    {
        var builder = writer.Begin(key, version);
        foreach (var (name, value) in fields)
        {
            if (writer.Schema.TryGetFieldIndex(name, out var field))
            {
                builder.Set(field, value switch
                {
                    double d => ScalarValue.FromFloat64(d),
                    long l => ScalarValue.FromInt64(l),
                    string s => ScalarValue.FromString(s),
                    float[] v => ScalarValue.FromVector(v),
                    _ => throw new ArgumentException(value.ToString()),
                });
            }
        }

        Assert.True(builder.Commit());
    }
}
