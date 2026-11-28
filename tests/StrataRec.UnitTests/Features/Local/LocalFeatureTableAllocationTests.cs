using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

[Collection(AllocationMeasurement.Name)]
public class LocalFeatureTableAllocationTests
{
    private static readonly LocalTableField[] Fields = AllTypes;

    [Theory]
    [InlineData(EntityKeyType.Int64, 500, false)]
    [InlineData(EntityKeyType.Int64, 500, true)]
    [InlineData(EntityKeyType.Int64, 5000, true)]
    [InlineData(EntityKeyType.String, 500, false)]
    [InlineData(EntityKeyType.String, 500, true)]
    public void LookupAndFill_AllocateNothing(EntityKeyType keyType, int keyCount, bool multiField)
    {
        var table = new LocalFeatureTable(Schema(keyType, fields: Fields));
        for (var id = 1; id <= 20_000; id++)
        {
            Write(table.Writer, Key(keyType, id), 1, Values(id));
        }

        var int64Keys = new long[keyCount];
        var stringKeys = new string?[keyCount];
        var random = new Random(7);
        for (var i = 0; i < keyCount; i++)
        {
            var id = i % 10 == 0 ? 50_000 + i : 1 + random.Next(20_000);
            int64Keys[i] = id;
            stringKeys[i] = Key(EntityKeyType.String, id).StringValue;
        }

        var absent = new ScalarValue[Fields.Length];
        for (var f = 0; f < Fields.Length; f++)
        {
            absent[f] = f % 2 == 0 ? Values(0)[Fields[f].Name] : ScalarValue.Missing;
        }

        var records = new LocalRecordRef[keyCount];
        var rows = new int[keyCount];
        for (var i = 0; i < keyCount; i++)
        {
            rows[i] = keyCount - 1 - i;
        }

        var best = long.MaxValue;
        for (var round = 0; round < 4; round++)
        {
            var memory = new RequestMemory();
            var columns = new ColumnBuffer[Fields.Length];
            var reads = new LocalFieldRead[Fields.Length];
            var nonFinite = new LocalFillCounts[Fields.Length];
            var filled = new ulong[Fields.Length][];
            for (var f = 0; f < Fields.Length; f++)
            {
                columns[f] = ColumnBuffer.Create(Fields[f].Type, keyCount, memory);
                filled[f] = new ulong[Bits.WordCount(keyCount)];
                reads[f] = new LocalFieldRead(f, columns[f], absent[f]);
            }

            var version = table.Pin();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var found = keyType == EntityKeyType.Int64 ? version.Lookup(int64Keys, records) : version.Lookup(stringKeys, records);
            if (multiField)
            {
                version.Fill(records, rows, reads, nonFinite);
            }
            else
            {
                for (var f = 0; f < Fields.Length; f++)
                {
                    version.Fill(f, records, rows, columns[f], absent[f], filled[f]);
                }
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(keyCount - (keyCount / 10), found);
            Assert.Equal(Values(int64Keys[1])["li64"], ColumnValues.Get(columns[Array.FindIndex(Fields, f => f.Name == "li64")], rows[1]));
            memory.Release();
            if (round > 0)
            {
                best = Math.Min(best, allocated);
            }
        }

        Assert.Equal(0, best);
    }

    [Fact]
    public void ALongStringField_FillsTheTablesInstances_WithoutAllocatingPerRow()
    {
        var fields = new[] { new LocalTableField("description", ColumnType.String) };
        var table = new LocalFeatureTable(Schema(EntityKeyType.Int64, fields: fields));
        for (var id = 1; id <= 20_000; id++)
        {
            var builder = table.Writer.Begin(id, 1);
            builder.SetString(0, $"item {id}: " + new string((char)('a' + (id % 26)), 300));
            Assert.True(builder.Commit());
        }

        var version = table.Pin();
        long Filled(int keyCount, out StringColumn column)
        {
            var keys = new long[keyCount];
            var rows = new int[keyCount];
            for (var i = 0; i < keyCount; i++)
            {
                keys[i] = 1 + ((i * 7919) % 20_000);
                rows[i] = i;
            }

            var records = new LocalRecordRef[keyCount];
            version.Lookup(keys, records);
            var memory = new RequestMemory();
            column = (StringColumn)ColumnBuffer.Create(ColumnType.String.AsNullable(), keyCount, memory);
            version.Fill(0, records, rows, column, ScalarValue.Missing);
            var best = long.MaxValue;
            for (var round = 0; round < 3; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                version.Fill(0, records, rows, column, ScalarValue.Missing);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            for (var i = 0; i < keyCount; i++)
            {
                Assert.Same(version.Read(0, records[i]).AsString(), column.Get(i));
            }

            return best;
        }

        Assert.Equal(0, Filled(10_000, out var first));
        Assert.Equal(0, Filled(500, out var second));
        Assert.Same(first.Get(0), second.Get(0));
        Assert.StartsWith("item 1: ", first.Get(0), StringComparison.Ordinal);
    }
}
