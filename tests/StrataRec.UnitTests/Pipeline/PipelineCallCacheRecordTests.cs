using System.Collections.Immutable;
using StrataRec.Core.Hashing;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Cache;
using StrataRec.Features.Local;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.UnitTests.Features;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The record a cached pipeline call is kept as: any result reads back as the result it was, a record that is not whole
/// is not read at all, and a reader judges a record's age by its own times.
/// </summary>
public sealed class PipelineCallCacheRecordTests
{
    private static readonly UInt128 Key = new(0x1234_5678_9abc_def0, 0x0fed_cba9_8765_4321);

    private sealed record Rig(PipelineCallCache Cache, ServiceCache Tiers, LocalFeatureTable Table, ManualClock Clock)
    {
        public void Apply() => Table.Cache!.WriteQueued(Table.Writer);
    }

    private static Rig NewRig(long ttlMs = 60_000, long staleIfErrorMs = 0)
    {
        var clock = new ManualClock();
        var options = new LocalFeatureTableOptions { MaxBytes = 8 << 20, Cache = new CacheTableOptions { TtlMs = CacheSettings.MaxMs, Clock = clock } };
        var table = new LocalFeatureTable(ServiceCache.Schema("p", PipelineCallCache.Entity), options);
        var tiers = new ServiceCache("p", table, table.Cache!.Policy, null, 0, string.Empty);
        return new Rig(new PipelineCallCache(tiers, new CachePolicy(ttlMs, staleIfErrorMs), null), tiers, table, clock);
    }

    /// <summary>
    /// A result that no tier keeps is not encoded: the request's own calls have it as it is, so the cache does not
    /// read it again to build a record for nobody.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AResultThatIsNotKept_IsNotEncoded(bool kept, int read)
    {
        var clock = new ManualClock();
        var options = new LocalFeatureTableOptions
        {
            MaxBytes = 8 << 20, Cache = new CacheTableOptions { TtlMs = CacheSettings.MaxMs, Memory = kept, Kept = kept, Clock = clock },
        };
        var table = new LocalFeatureTable(ServiceCache.Schema("p", PipelineCallCache.Entity), options);
        var cache = new PipelineCallCache(new ServiceCache("p", table, table.Cache!.Policy, null, 0, string.Empty), CachePolicy.Unlimited, null);
        var columns = new ResultColumns([EntityKey.FromInt64(7)], ["score"], [ColumnType.Float64], [[ScalarValue.FromFloat64(0.5)]]);
        var exposures = new CountedExposures();

        cache.Write(Key, columns, ResultValues.Empty, new CachedFrom("req", 1, "feed/n#0"), exposures, clock.NowMs);

        Assert.Equal(read, exposures.Read);
    }

    /// <summary>No exposures, counting how often their number is asked for.</summary>
    private sealed class CountedExposures : IReadOnlyList<CallExposure>
    {
        public int Read { get; private set; }

        public int Count
        {
            get
            {
                Read++;
                return 0;
            }
        }

        public CallExposure this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<CallExposure> GetEnumerator() => Enumerable.Empty<CallExposure>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static readonly ColumnType[] Types =
    [
        ColumnType.Bool.AsNullable(), ColumnType.Int64.AsNullable(), ColumnType.Int32.AsNullable(), ColumnType.Float64.AsNullable(), ColumnType.Float32.AsNullable(),
        ColumnType.String.AsNullable(), ColumnType.List(StorageKind.Bool, nullable: true), ColumnType.List(StorageKind.Int64, nullable: true),
        ColumnType.List(StorageKind.Float64, nullable: true), ColumnType.List(StorageKind.Float32, nullable: true), ColumnType.List(StorageKind.String, nullable: true),
    ];

    /// <summary>A value of <paramref name="type"/>, or none: what a result's column may hold on a row.</summary>
    private static ScalarValue Value(Random random, ColumnType type, bool mayBeMissing)
    {
        if (mayBeMissing && random.Next(5) == 0)
        {
            return ScalarValue.Missing;
        }

        var length = random.Next(4);
        return type.Kind switch
        {
            StorageKind.Bool => ScalarValue.FromBool(random.Next(2) == 0),
            StorageKind.Int64 or StorageKind.Int32 => ScalarValue.FromInt64(random.Next(-5, 5)),
            StorageKind.Float64 => ScalarValue.FromFloat64(random.Next(3) == 0 ? double.NaN : random.NextDouble()),
            StorageKind.Float32 => ScalarValue.FromFloat32(random.NextSingle()),
            StorageKind.String => ScalarValue.FromString(random.Next(3) == 0 ? string.Empty : "s" + random.Next(100)),
            _ => type.Element switch
            {
                StorageKind.Bool => ScalarValue.FromBoolList(Enumerable.Range(0, length).Select(_ => random.Next(2) == 0).ToArray()),
                StorageKind.Int64 => ScalarValue.FromInt64List(Enumerable.Range(0, length).Select(_ => random.NextInt64()).ToArray()),
                StorageKind.Float64 => ScalarValue.FromFloat64List(Enumerable.Range(0, length).Select(_ => random.NextDouble()).ToArray()),
                StorageKind.Float32 => ScalarValue.FromFloat32List(Enumerable.Range(0, length).Select(_ => random.NextSingle()).ToArray()),
                _ => ScalarValue.FromStringList(Enumerable.Range(0, length).Select(i => "e" + i).ToArray()),
            },
        };
    }

    private static (PipelineInterface Shape, ResultColumns Columns, ResultValues Values, CallExposure[] Exposures) Result(Random random)
    {
        var rows = random.Next(4) == 0 ? 0 : random.Next(1, 30);
        var strings = random.Next(2) == 0;
        var ids = Enumerable.Range(0, rows).Select(r => strings ? EntityKey.FromString("k" + r) : EntityKey.FromInt64(random.NextInt64())).ToArray();
        var types = Enumerable.Range(0, random.Next(5)).Select(_ => Types[random.Next(Types.Length)]).ToArray();
        var names = types.Select((_, c) => "c" + c).ToArray();

        // Some columns have a value on every row (kept as the values and one tag), some do not.
        var columns = types.Select(t =>
        {
            var gaps = random.Next(2) == 0;
            return Enumerable.Range(0, rows).Select(_ => Value(random, t, gaps)).ToArray();
        }).ToArray();
        var valueTypes = Enumerable.Range(0, random.Next(4)).Select(_ => Types[random.Next(Types.Length)]).ToArray();
        var values = valueTypes.Select((t, v) => new ResultValue("v" + v, t, Value(random, t, mayBeMissing: true))).ToArray();
        var exposures = Enumerable.Range(0, random.Next(3)).Select(e => new CallExposure(
            "pipeline" + e, "manifest" + e, e == 0 ? null : "p/n#" + e, Enumerable.Range(0, random.Next(3)).Select(a => $"exp{a}/arm").ToArray(),
            Enumerable.Range(0, random.Next(2)).Select(a => "exp" + a).ToArray(),
            random.Next(3) == 0 ? new CachedFrom("earlier-" + e, random.NextInt64(), "other/n#" + e) : null)).ToArray(); // one a cache answered when the result was computed
        var shape = new PipelineInterface
        {
            PipelineId = "p",
            KeyType = strings ? EntityKeyType.String : EntityKeyType.Int64,
            Inputs = new RequestSchema("p", new Dictionary<string, ColumnType>()),
            Columns = [.. names.Select((n, c) => new PipelineOutput(n, types[c]))],
            Values = [.. values.Select(v => new PipelineOutput(v.Name, v.Type))],
        };
        return (shape, new ResultColumns(ids, names, types, columns), ResultValues.OfOutput(values), exposures);
    }

    /// <summary>A value as what it is, not only as what it prints: a float32 is not the float64 that prints alike.</summary>
    private static string Describe(in ScalarValue value) => $"{value.Kind}/{value.ListElement}/{value.IsFloat32}/{value.IsFloat32List}/{value}";

    private static string Describe(ResultColumns columns, ResultValues values, IReadOnlyList<CallExposure> exposures)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(string.Join(",", columns.Ids.ToArray().Select(k => k.IsString ? "s:" + k.StringValue : "i:" + k.Int64Value)));
        for (var c = 0; c < columns.Names.Count; c++)
        {
            text.Append(columns.Names[c]).Append(':').Append(columns.Types[c]).Append('=');
            foreach (var value in columns.Column(c))
            {
                text.Append(Describe(value)).Append(';');
            }

            text.AppendLine();
        }

        foreach (var value in values.All)
        {
            text.AppendLine($"{value.Name}:{value.Type}={Describe(value.Value)}");
        }

        foreach (var exposure in exposures)
        {
            text.AppendLine($"{exposure.PipelineId}|{exposure.ManifestId}|{exposure.Path ?? "-"}|{string.Join(",", exposure.Arms)}|{string.Join(",", exposure.Triggered)}|{exposure.From}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Any result reads back as it was written: keys of either type, no rows, columns with and without a value on every
    /// row, lists, values, the runs that computed it. Generated; <c>STRATAREC_GENERATED_SEEDS</c> runs more.
    /// </summary>
    [Fact]
    public void AnyResult_ReadsBackAsTheResultItWas()
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("STRATAREC_GENERATED_SEEDS"), out var given) ? given : 300;
        var rig = NewRig();
        for (var seed = 0; seed < seeds; seed++)
        {
            var random = new Random(seed);
            var (shape, columns, values, exposures) = Result(random);
            var from = new CachedFrom("req-" + seed, random.NextInt64(), "feed/n#" + seed);
            var key = new UInt128((ulong)random.NextInt64(), (ulong)seed);

            rig.Cache.Write(key, columns, values, from, exposures, rig.Clock.NowMs);
            rig.Apply();
            var read = rig.Cache.ReadMemory(key, shape);

            Assert.True(read.Fresh is not null, $"seed {seed}: the result was not read back");
            Assert.Equal(from, read.Fresh.From);
            Assert.Equal(rig.Clock.NowMs, read.Fresh.WrittenMs);
            Assert.True(Describe(columns, values, exposures) == Describe(read.Fresh.Columns, read.Fresh.Values, read.Fresh.Exposures),
                $"seed {seed}:\n{Describe(columns, values, exposures)}\nread back as\n{Describe(read.Fresh.Columns, read.Fresh.Values, read.Fresh.Exposures)}");
        }
    }

    /// <summary>
    /// A record that does not hold exactly what its counts say (cut short, with a value left over, of another kind or
    /// other counts than the reader's pipeline returns) is a miss: nothing of it is read as a value.
    /// </summary>
    [Theory]
    [InlineData("kind")]
    [InlineData("rows more")]
    [InlineData("rows fewer")]
    [InlineData("columns")]
    [InlineData("values")]
    [InlineData("exposures")]
    [InlineData("layout cut")]
    [InlineData("layout longer")]
    [InlineData("string dropped")]
    [InlineData("string added")]
    [InlineData("int64 dropped")]
    [InlineData("int64 added")]
    [InlineData("float64 added")]
    [InlineData("bool added")]
    [InlineData("int32 added")]
    [InlineData("tag unknown")]
    [InlineData("list length")]
    public void ARecordThatIsNotWhole_IsAMiss(string damage)
    {
        var rig = NewRig();
        var types = new[] { ColumnType.Float64, ColumnType.List(StorageKind.Int64, nullable: true) };
        var columns = new ResultColumns(
            [EntityKey.FromInt64(7), EntityKey.FromInt64(8)], ["score", "tags"], types,
            [[ScalarValue.FromFloat64(0.5), ScalarValue.FromFloat64(0.25)], [ScalarValue.FromInt64List([1, 2]), ScalarValue.Missing]]);
        var values = ResultValues.OfOutput([new ResultValue("count", ColumnType.Int64, ScalarValue.FromInt64(2))]);
        var shape = new PipelineInterface
        {
            PipelineId = "p", KeyType = EntityKeyType.Int64, Inputs = new RequestSchema("p", new Dictionary<string, ColumnType>()),
            Columns = [new PipelineOutput("score", types[0]), new PipelineOutput("tags", types[1])], Values = [new PipelineOutput("count", ColumnType.Int64)],
        };
        var builder = new ServiceRecordBuilder(rig.Tiers, 1, rig.Clock.NowMs);
        PipelineCallCache.Encode(builder, columns, values, new CachedFrom("req", 1, "feed/n#0"), [new CallExposure("p", "m", null, ["e/a"], ["e"])]);

        // The layout: kind, rows, id kind, columns, values, exposures; then score's tag (float64 on every row), tags' (per
        // value: a list of two, then none), count's, and the exposure's: no path, a run of the computing request, its counts.
        Assert.Equal(new[] { 3, 2, 1, 2, 1, 1, 3, 0, 7, 2, 0, 2, 0, 0, 1, 1 }, builder.Layout);
        switch (damage)
        {
            case "kind": builder.Layout[0] = 1; break;
            case "rows more": builder.Layout[1] = 3; break;
            case "rows fewer": builder.Layout[1] = 1; break;
            case "columns": builder.Layout[3] = 1; break;
            case "values": builder.Layout[4] = 0; break;
            case "exposures": builder.Layout[5] = 2; break;
            case "layout cut": builder.Layout.RemoveAt(builder.Layout.Count - 1); break;
            case "layout longer": builder.Layout.Add(0); break;
            case "string dropped": builder.Strings.RemoveAt(builder.Strings.Count - 1); break;
            case "string added": builder.Strings.Add("x"); break;
            case "int64 dropped": builder.Int64s.RemoveAt(builder.Int64s.Count - 1); break;
            case "int64 added": builder.Int64s.Add(9); break;
            case "float64 added": builder.Float64s.Add(9); break;
            case "bool added": builder.Bools.Add(true); break;
            case "int32 added": builder.Int32s.Add(9); break;
            case "tag unknown": builder.Layout[6] = 99; break;
            case "list length": builder.Layout[9] = 3; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }

        builder.End(Key);
        builder.WriteBack();
        rig.Apply();

        Assert.Equal(1, rig.Table.Pin().RecordCount);
        Assert.Equal(default, rig.Cache.ReadMemory(Key, shape));
    }

    /// <summary>
    /// A record whose counts add up, but not to a result of the reader's pipeline, is a miss too: it was written for
    /// another shape, or changed so that every list is still read to its end. Read as it was, two rows of a fixed-length
    /// list came back as lists of one and three elements, a string as the value of an int64 column, and string ids as
    /// the rows of a pipeline with int64 keys.
    /// </summary>
    [Theory]
    [InlineData("list lengths moved")]
    [InlineData("a string in an int64 column")]
    [InlineData("a list in a scalar column")]
    [InlineData("string ids")]
    [InlineData("a value of another type")]
    public void ARecordThatIsNotAResultOfTheReadersShape_IsAMiss(string damage)
    {
        var rig = NewRig();
        var pair = ColumnType.List(StorageKind.Int64, fixedLength: 2);
        var written = damage switch
        {
            "a string in an int64 column" => ColumnType.String,
            "a list in a scalar column" => ColumnType.List(StorageKind.Int64),
            _ => pair,
        };
        var read = damage is "a string in an int64 column" or "a list in a scalar column" ? ColumnType.Int64 : pair;
        ScalarValue[] cells = written.IsList
            ? [ScalarValue.FromInt64List([10, 11]), ScalarValue.FromInt64List([20, 21])]
            : [ScalarValue.FromString("wrong"), ScalarValue.FromString("too")];
        EntityKey[] ids = damage == "string ids" ? [EntityKey.FromString("a"), EntityKey.FromString("b")] : [EntityKey.FromInt64(7), EntityKey.FromInt64(8)];
        var columns = new ResultColumns(ids, ["c"], [written], [cells]);
        var total = damage == "a value of another type" ? ScalarValue.FromFloat64(1.5) : ScalarValue.FromInt64(2);
        var values = ResultValues.OfOutput([new ResultValue("count", ColumnType.Int64, total)]);
        var shape = new PipelineInterface
        {
            PipelineId = "p", KeyType = EntityKeyType.Int64, Inputs = new RequestSchema("p", new Dictionary<string, ColumnType>()),
            Columns = [new PipelineOutput("c", read)], Values = [new PipelineOutput("count", ColumnType.Int64)],
        };
        var builder = new ServiceRecordBuilder(rig.Tiers, 1, rig.Clock.NowMs);
        PipelineCallCache.Encode(builder, columns, values, new CachedFrom("req", 1, "feed/n#0"), []);
        if (damage == "list lengths moved")
        {
            // The column's tag (per value), then each value's tag and length: 2 and 2 become 1 and 3, the elements stay.
            Assert.Equal(new[] { 0, 7, 2, 7, 2 }, builder.Layout.GetRange(6, 5));
            builder.Layout[8] = 1;
            builder.Layout[10] = 3;
        }

        builder.End(Key);
        builder.WriteBack();
        rig.Apply();

        Assert.Equal(1, rig.Table.Pin().RecordCount);
        Assert.Equal(default, rig.Cache.ReadMemory(Key, shape));
    }

    /// <summary>The same record, undamaged, is read: the damage above is what makes each a miss.</summary>
    [Fact]
    public void TheRecordOfTheDamageCases_IsReadWhenWhole_AndNotAsAResultOfAnotherShape()
    {
        var rig = NewRig();
        var columns = new ResultColumns([EntityKey.FromInt64(7)], ["score"], [ColumnType.Float64], [[ScalarValue.FromFloat64(0.5)]]);
        var inputs = new RequestSchema("p", new Dictionary<string, ColumnType>());
        var shape = new PipelineInterface { PipelineId = "p", KeyType = EntityKeyType.Int64, Inputs = inputs, Columns = [new PipelineOutput("score", ColumnType.Float64)] };
        var wider = new PipelineInterface
        {
            PipelineId = "p", KeyType = EntityKeyType.Int64, Inputs = inputs,
            Columns = [new PipelineOutput("score", ColumnType.Float64), new PipelineOutput("more", ColumnType.Float64)],
        };

        rig.Cache.Write(Key, columns, ResultValues.Empty, new CachedFrom("req", 1, "feed/n#0"), [], rig.Clock.NowMs);
        rig.Apply();

        Assert.NotNull(rig.Cache.ReadMemory(Key, shape).Fresh);
        Assert.Equal(default, rig.Cache.ReadMemory(Key, wider));
    }

    /// <summary>
    /// A node judges a record by its own times, from when the run that computed it started: fresh within its ttl, kept
    /// for a failure within its stale window, and nothing after; another node with a longer ttl still has it fresh.
    /// </summary>
    [Fact]
    public void ANodeJudgesARecordByItsOwnTimes()
    {
        var rig = NewRig(ttlMs: 1_000, staleIfErrorMs: 5_000);
        var patient = new PipelineCallCache(rig.Tiers, new CachePolicy(60_000), null);
        var columns = new ResultColumns([EntityKey.FromInt64(7)], ["score"], [ColumnType.Float64], [[ScalarValue.FromFloat64(0.5)]]);
        var shape = new PipelineInterface
        {
            PipelineId = "p", KeyType = EntityKeyType.Int64, Inputs = new RequestSchema("p", new Dictionary<string, ColumnType>()),
            Columns = [new PipelineOutput("score", ColumnType.Float64)],
        };
        rig.Cache.Write(Key, columns, ResultValues.Empty, new CachedFrom("req", 1, "feed/n#0"), [], rig.Clock.NowMs);
        rig.Apply();

        rig.Clock.AdvanceMs(800); // within the ttl whatever its jitter (±10%)
        Assert.NotNull(rig.Cache.ReadMemory(Key, shape).Fresh);

        rig.Clock.AdvanceMs(1_200); // 2,000: past the ttl, within the stale window
        var stale = rig.Cache.ReadMemory(Key, shape);
        Assert.True(stale is { Fresh: null, Stale: not null });
        Assert.True(rig.Cache.Usable(Key, stale.Stale));
        Assert.NotNull(patient.ReadMemory(Key, shape).Fresh);

        rig.Clock.AdvanceMs(5_000); // 7,000: past both
        Assert.False(rig.Cache.Usable(Key, stale.Stale)); // judged when it is to be served, not when it was read
        Assert.Equal(default, rig.Cache.ReadMemory(Key, shape));
        Assert.NotNull(patient.ReadMemory(Key, shape).Fresh);
    }

    /// <summary>
    /// Any value is part of a key as what it is: a missing input, an empty string and an empty list are three inputs, a
    /// list is its elements in order, and where one value ends and the next begins is part of it.
    /// </summary>
    [Fact]
    public void AnyValueHashes_AsWhatItIs()
    {
        static ulong Hash(params ScalarValue[] values)
        {
            var builder = default(StableHash.Builder);
            try
            {
                foreach (var value in values)
                {
                    builder.AddAny(value);
                }

                return builder.Finish();
            }
            finally
            {
                builder.Dispose();
            }
        }

        var different = new[]
        {
            Hash(ScalarValue.Missing), Hash(ScalarValue.FromString(string.Empty)), Hash(ScalarValue.FromStringList([])), Hash(ScalarValue.FromBool(false)),
            Hash(ScalarValue.FromInt64(0)), Hash(ScalarValue.FromFloat64(0)), Hash(ScalarValue.FromInt64List([1, 2])), Hash(ScalarValue.FromInt64List([2, 1])),
            Hash(ScalarValue.FromInt64List([1]), ScalarValue.FromInt64List([2])), Hash(ScalarValue.FromInt64(1), ScalarValue.FromInt64(2)),
            Hash(ScalarValue.FromStringList(["ab"])), Hash(ScalarValue.FromStringList(["a", "b"])), Hash(ScalarValue.FromFloat64List([1, 2])),
            Hash(ScalarValue.FromBoolList([true])), Hash(ScalarValue.Missing, ScalarValue.Missing), Hash(),
        };

        Assert.Equal(different.Length, different.Distinct().Count());
        Assert.Equal(Hash(ScalarValue.FromInt64List([1, 2])), Hash(ScalarValue.FromInt64List([1, 2])));
        Assert.Equal(Hash(ScalarValue.FromFloat32(0.5f)), Hash(ScalarValue.FromFloat64(0.5))); // the declared type says its width
    }
}
