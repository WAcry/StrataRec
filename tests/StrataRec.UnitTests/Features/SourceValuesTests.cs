using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Catalog;
using StrataRec.Features.Connectors;
using StrataRec.Features.Local;
using StrataRec.Features.Runtime;
using StrataRec.UnitTests.Features.Cache;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <c>stratarec.source.values</c> (design 13.1, a source's missing ratio): what a source had of the values it was asked
/// for, per feature, in each supply mode. Each test binds sources of its own names and reads only their measurements.
/// </summary>
[Collection(MetricMeasurement.Name)]
public class SourceValuesTests
{
    private static readonly EntityDefinition Author = FeatureTestRig.ForeignKey("author", "item.author_id");

    [Fact]
    public async Task AFetch_CountsEachDistinctKeyOncePerFeature_PresentWhereTheConnectorWroteTheField()
    {
        // Keys 1-5 in chunks of two. clicks: written for 1-3 (3 as NaN, which the source did return); age: for 1 only.
        var connector = ScriptedConnector.Values((key, field) => field switch
        {
            "clicks" when key <= 2 => key * 1.0,
            "clicks" when key == 3 => double.NaN,
            "age" when key == 1 => 7.0,
            _ => null,
        });
        var rig = new FeatureTestRig().Source("values_fetch", connector, maxBatch: 2);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_fetch", ScalarValue.FromFloat64(0));
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "values_fetch");
        rig.Build();
        using var values = new SourceValues("values_fetch");

        var request = rig.NewRequest([1, 2, 2, 3, 4, 5, 5]);
        var result = await request.EnsureAsync(clicks, age);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(0.0, request.Value(clicks, 4).AsFloat64()); // key 4 took the default: still missing at the source
        Assert.Equal((3, 2), values.Of("item.clicks"));
        Assert.Equal((1, 4), values.Of("item.age"));
        Assert.Equal(4, values.Adds); // one add per feature and outcome for the fetch, not one per chunk
        Assert.All(values.TagSets, tags =>
        {
            Assert.Equal("feature", tags[StrataRecTags.SourceKind]);
            Assert.Equal(4, tags.Count);
        });
    }

    [Fact]
    public async Task AFetch_DoesNotCountTheKeysOfAChunkThatFailed_AndCountsAPartialChunksKeys()
    {
        // Chunks [1, 2], [3, 4], [5]: the first answers, the second fails, the third answers in part, with nothing for 5.
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            switch (keys.CandidateKeys.Span[0].Int64Value)
            {
                case 3:
                    return ValueTask.FromResult(FetchStatus.Failed("values_partial is down"));
                case 5:
                    return ValueTask.FromResult(new FetchStatus(FetchOutcome.Partial, 1, "key 5 failed"));
                default:
                    ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key * 1.0);
                    return ValueTask.FromResult(FetchStatus.Ok);
            }
        });
        var rig = new FeatureTestRig().Source("values_partial", connector, maxBatch: 2);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_partial");
        rig.Build();
        using var values = new SourceValues("values_partial");

        var result = await rig.NewRequest([1, 2, 3, 4, 5]).EnsureAsync(clicks);

        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.Equal((2, 1), values.Of("item.clicks")); // keys 3 and 4 are an error, not missing values
    }

    [Fact]
    public async Task AFetchThatFailsWhole_CountsNothing()
    {
        var connector = new ScriptedConnector((_, _, _, _) => ValueTask.FromResult(FetchStatus.Failed("values_down is down")));
        var rig = new FeatureTestRig().Source("values_down", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_down");
        rig.Build();
        using var values = new SourceValues("values_down");

        var result = await rig.NewRequest([1, 2]).EnsureAsync(clicks);

        Assert.True(result.Failed);
        Assert.Equal(0, values.Adds);
    }

    [Theory]
    [InlineData(1001L, 1, 1)]
    [InlineData(null, 0, 0)] // no user: the source is asked for nothing
    public async Task AContextFetch_CountsItsOneKey(long? userId, int ctrPresent, int tenureMissing)
    {
        var connector = ScriptedConnector.Values((_, field) => field == "ctr" ? 0.05 : null);
        var rig = new FeatureTestRig().Source("values_user", connector);
        var ctr = rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float64, "values_user");
        var tenure = rig.Physical("user.tenure", FeatureTestRig.User, ColumnType.Float64, "values_user", ScalarValue.FromFloat64(0));
        rig.Build();
        using var values = new SourceValues("values_user");

        var request = rig.NewRequest([1], userId);
        request.Fetcher.StartContextFetch([ctr, tenure], CancellationToken.None);
        var result = await request.EnsureAsync(ctr, tenure);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal((ctrPresent, 0), values.Of("user.ctr"));
        Assert.Equal((0, tenureMissing), values.Of("user.tenure"));
    }

    [Fact]
    public async Task ALocalRead_CountsAnUnknownKeyMissingForEveryFeature_ARecordWithoutAFieldMissingForIt_AndNoRowWithoutAKey()
    {
        // Item 1 has every field and author 10; item 2 has clicks only; item 3 has no record. Only item 1 has an author key.
        var rig = new FeatureTestRig()
            .LocalSource("values_items", writer =>
            {
                Write(writer, 1, new() { ["clicks"] = 3.0, ["age"] = 1.5, ["author_id"] = 10L });
                Write(writer, 2, new() { ["clicks"] = 8.0 });
            })
            .LocalSource("values_authors", writer => Write(writer, 10, new() { ["followers"] = 5.0 }));
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_items", ScalarValue.FromFloat64(0));
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "values_items");
        rig.Physical("item.author_id", FeatureTestRig.Item, ColumnType.Int64, "values_items");
        var followers = rig.Physical("author.followers", Author, ColumnType.Float64, "values_authors", ScalarValue.FromFloat64(-1));
        rig.Build();
        using var items = new SourceValues("values_items");
        using var authors = new SourceValues("values_authors");

        var result = await rig.NewRequest([1, 2, 3, 3]).EnsureAsync(clicks, age, followers);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal((2, 1), items.Of("item.clicks"));
        Assert.Equal((1, 2), items.Of("item.age"));
        Assert.Equal((1, 2), items.Of("item.author_id")); // read as the key of author.followers
        Assert.Equal((1, 0), authors.Of("author.followers")); // items 2 and 3 have no author key: not asked
    }

    [Theory]
    [InlineData(1001L, 1, 1)] // a record without tenure
    [InlineData(9999L, 0, 2)] // no record
    [InlineData(null, 0, 0)] // no key: not asked
    public async Task ALocalContextRead_CountsItsOneKey(long? userId, int present, int missing)
    {
        var rig = new FeatureTestRig().LocalSource("values_local_user", writer => Write(writer, 1001, new() { ["ctr"] = 0.05 }));
        var ctr = rig.Physical("user.ctr", FeatureTestRig.User, ColumnType.Float64, "values_local_user");
        var tenure = rig.Physical("user.tenure", FeatureTestRig.User, ColumnType.Float64, "values_local_user");
        rig.Build();
        using var values = new SourceValues("values_local_user");

        var request = rig.NewRequest([1], userId);
        request.Fetcher.StartContextFetch([ctr, tenure], CancellationToken.None);
        await request.EnsureAsync(ctr, tenure);

        var (ctrPresent, ctrMissing) = values.Of("user.ctr");
        var (tenurePresent, tenureMissing) = values.Of("user.tenure");
        Assert.Equal((present, missing), (ctrPresent + tenurePresent, ctrMissing + tenureMissing));
        Assert.Equal(0, tenurePresent);
    }

    [Fact]
    public async Task ACacheRead_CountsLocalHitsAndResolvedMisses_AndNotAKeyWhoseResolutionFailed()
    {
        var fail = false;
        var connector = new ScriptedConnector((keys, fields, writer, _) =>
        {
            if (fail)
            {
                return ValueTask.FromResult(FetchStatus.Failed("values_cache is down"));
            }

            ScriptedConnector.WriteAll(keys, fields, writer, (key, _) => key == 2 ? null : key * 1.0);
            return ValueTask.FromResult(FetchStatus.Ok);
        });
        var rig = new FeatureTestRig().CacheSource("values_cache", connector);
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_cache");
        rig.Build();
        using var values = new SourceValues("values_cache");

        // Two misses the connector resolves: it has nothing for 2, which is cached as absent.
        await rig.NewRequest([1, 2]).EnsureAsync(clicks);
        Assert.Equal((1, 1), values.Of("item.clicks"));
        rig.WriteBack("values_cache");

        // Local hits 1 and 2 (still missing), and a miss, 3, the connector resolves.
        values.Clear();
        await rig.NewRequest([1, 2, 3]).EnsureAsync(clicks);
        Assert.Equal(new long[] { 3 }, connector.Calls.Last().Keys.Select(k => k.Int64Value));
        Assert.Equal((2, 1), values.Of("item.clicks"));
        Assert.Equal(2, values.Adds); // one add per outcome for the read, its hits and its resolved miss together
        rig.WriteBack("values_cache");

        // A local hit, and a miss the connector fails with no stale record to serve: an error, not a missing value.
        fail = true;
        values.Clear();
        var result = await rig.NewRequest([1, 4]).EnsureAsync(clicks);
        Assert.Equal(FeatureState.Degraded, result.State);
        Assert.Equal((1, 0), values.Of("item.clicks"));

        // Every key fresh: a local read, counted like one.
        values.Clear();
        await rig.NewRequest([3, 2, 1]).EnsureAsync(clicks);
        Assert.Equal((2, 1), values.Of("item.clicks"));
    }

    [Fact]
    public async Task ACacheRead_CountsTheKeysTheDistributedTierAnswered()
    {
        var store = new FakeCacheStore();
        static ScriptedConnector Connector() => ScriptedConnector.Values((key, _) => key == 2 ? null : key * 1.0);
        var writer = new FeatureTestRig().CacheSource("values_shared", Connector(), distributed: store);
        var writerClicks = writer.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_shared");
        writer.Build();
        await writer.NewRequest([1, 2]).EnsureAsync(writerClicks);
        await store.WhenWrittenAsync(2); // 2 as an absent record

        var readerConnector = Connector();
        var reader = new FeatureTestRig().CacheSource("values_shared", readerConnector, distributed: store); // an empty table
        var readerClicks = reader.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_shared");
        reader.Build();
        using var values = new SourceValues("values_shared");
        await reader.NewRequest([1, 2, 3]).EnsureAsync(readerClicks);

        Assert.Equal(new long[] { 3 }, Assert.Single(readerConnector.Calls).Keys.Select(k => k.Int64Value));
        Assert.Equal((2, 1), values.Of("item.clicks"));
    }

    [Fact]
    public async Task AReplay_CountsNothing()
    {
        // A replay is not traffic (design 13.4): neither its fetches nor the local groups it answers through the fetch path.
        var rig = new FeatureTestRig()
            .Source("values_replay_fetch", ScriptedConnector.Values((key, _) => key * 1.0))
            .LocalSource("values_replay_local", writer => Write(writer, 1, new() { ["age"] = 2.0 }));
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_replay_fetch");
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "values_replay_local");
        rig.Build();
        using var fetched = new SourceValues("values_replay_fetch");
        using var local = new SourceValues("values_replay_local");

        var request = rig.NewRequest([1, 2], hook: new FeatureCallHookTests.ListHook(replays: true));
        var result = await request.EnsureAsync(clicks, age);

        Assert.Equal(FeatureState.Ok, result.State);
        Assert.Equal(20.0, request.Value(age, 1).AsFloat64()); // answered by the replaying hook
        Assert.Equal(0, fetched.Adds + local.Adds);
    }

    [Fact]
    public async Task WithoutAListenerForTheInstrument_NothingIsCounted_NorAreItsTagsBuilt()
    {
        var rig = new FeatureTestRig()
            .Source("values_quiet", ScriptedConnector.Values((key, _) => key * 1.0))
            .LocalSource("values_quiet_local", writer => Write(writer, 1, new() { ["age"] = 2.0 }));
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Float64, "values_quiet");
        var age = rig.Physical("item.age", FeatureTestRig.Item, ColumnType.Float64, "values_quiet_local");
        rig.Build();
        long keys = 0, values = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name is StrataRecMetrics.Names.SourceKeys or StrataRecMetrics.Names.LocalKeys)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name == StrataRecMetrics.Names.SourceValues)
            {
                Interlocked.Increment(ref values);
            }
            else
            {
                Interlocked.Increment(ref keys);
            }
        });
        listener.Start();

        Assert.False(StrataRecMetrics.SourceValues.Enabled);
        await rig.NewRequest([1, 2]).EnsureAsync(clicks, age);

        Assert.True(Interlocked.Read(ref keys) >= 2); // the sources were asked: a fetch, and a local read (its hit and miss)
        Assert.Equal(0, Interlocked.Read(ref values));
        var tags = typeof(FeatureRuntimeInfo).GetField("_valueTags", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(tags.GetValue(rig.Plan.Features[clicks]));
        Assert.Null(tags.GetValue(rig.Plan.Features[age]));
    }

    private static void Write(IFeatureTableWriter writer, long key, Dictionary<string, object> fields)
    {
        var builder = writer.Begin(key, 1);
        foreach (var (name, value) in fields)
        {
            builder.Set(writer.Schema.FieldIndex(name), value switch
            {
                double d => ScalarValue.FromFloat64(d),
                long l => ScalarValue.FromInt64(l),
                _ => throw new ArgumentException(value.ToString()),
            });
        }

        Assert.True(builder.Commit());
    }

    /// <summary>The measurements of <c>stratarec.source.values</c> for one source, summed by feature and outcome.</summary>
    private sealed class SourceValues : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Dictionary<string, object?>> _tagSets = new();
        private readonly ConcurrentDictionary<(string Feature, string Outcome), long> _totals = new();
        private int _adds;

        public SourceValues(string source)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name && instrument.Name == StrataRecMetrics.Names.SourceValues)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value;
                }

                if (!Equals(copy.GetValueOrDefault(StrataRecTags.Source), source))
                {
                    return;
                }

                Interlocked.Increment(ref _adds);
                _tagSets.Enqueue(copy);
                _totals.AddOrUpdate(((string)copy[StrataRecTags.Feature]!, (string)copy[StrataRecTags.Outcome]!), value, (_, total) => total + value);
            });
            _listener.Start();
        }

        public int Adds => Volatile.Read(ref _adds);

        public IReadOnlyCollection<Dictionary<string, object?>> TagSets => _tagSets;

        public (int Present, int Missing) Of(string feature) =>
            ((int)_totals.GetValueOrDefault((feature, "present")), (int)_totals.GetValueOrDefault((feature, "missing")));

        public void Clear()
        {
            _totals.Clear();
            _tagSets.Clear();
            Interlocked.Exchange(ref _adds, 0);
        }

        public void Dispose() => _listener.Dispose();
    }
}
