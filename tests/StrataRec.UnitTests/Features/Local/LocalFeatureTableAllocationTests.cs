using StrataRec.Core.Columns;
using StrataRec.Core.Keys;
using StrataRec.Core.Memory;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Local;
using static StrataRec.UnitTests.Features.Local.LocalTableTestSupport;

namespace StrataRec.UnitTests.Features.Local;

/// <summary>
/// Reading a local table on the request path allocates nothing (design 4.8, 6.6): the lookup writes into the caller's
/// span, and filling writes into column buffers whose arrays come from the request memory. Two costs belong to the column
/// types and are outside the claim: a <c>text</c> value becomes a string (until text columns store UTF-8, design 4.4), and
/// a string column's dictionary grows by values it has not seen (here it is preset, as a plan could size it).
/// </summary>
[Collection(AllocationMeasurement.Name)]
public class LocalFeatureTableAllocationTests
{
    private static readonly LocalTableField[] Fields = AllTypes.Where(f => f.Type.Kind != StorageKind.Text).ToArray();

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
            var values = Values(id);
            values.Remove("t");
            Write(table.Writer, Key(keyType, id), 1, values);
        }

        // Every tenth key is absent; every field is read, with a default for half of them.
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
            var nonFinite = new int[Fields.Length];
            var filled = new ulong[Fields.Length][];
            for (var f = 0; f < Fields.Length; f++)
            {
                columns[f] = ColumnBuffer.Create(Fields[f].Type, keyCount, memory);
                filled[f] = new ulong[Bits.WordCount(keyCount)];
                if (columns[f] is DictStringColumn strings)
                {
                    for (var c = 0; c < 5; c++)
                    {
                        strings.GetOrAddCode("cat-" + c);
                    }
                }

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
}
