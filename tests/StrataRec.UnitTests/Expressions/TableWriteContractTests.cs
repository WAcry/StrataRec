using StrataRec.Core.Columns;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.UnitTests.Core;

namespace StrataRec.UnitTests.Expressions;

/// <summary>
/// An expression writing into a real table slot: the rows it wrote a value into are present, the rows it wrote as
/// missing and the rows it did not write are missing. The other expression tests write into buffers made directly,
/// which count every row valid and so cannot tell these apart.
/// </summary>
public class TableWriteContractTests
{
    private const int Physical = 130; // three validity words

    public static TheoryData<string, string, string> Cases()
    {
        var cases = new TheoryData<string, string, string>();
        foreach (var storage in TableWrites.EveryStorageType)
        {
            foreach (var type in new[] { storage, storage + "?" })
            {
                foreach (var domain in new[] { "identity", "gather" })
                {
                    foreach (var shape in new[] { "column", "broadcast", "in place" })
                    {
                        if (shape != "in place" || type.EndsWith('?'))
                        {
                            cases.Add(type, domain, shape);
                        }
                    }
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AnExpressionWritingIntoATableSlot_MarksTheRowsItWrote_AndLeavesEveryOtherRowMissing(string type, string domain, string shape)
    {
        var columnType = ColumnType.Parse(type);
        var broadcast = TableWrites.Sample(columnType, 999);
        var f = new ExprFixture(Physical, new DirtyPool())
            .Col("col.src", type).Col("col.dst", type)
            .Param("param.v", type, broadcast)
            .Build(Physical);

        var source = Enumerable.Range(0, Physical)
            .Select(r => columnType.Nullable && r % 4 == 1 ? ScalarValue.Missing : TableWrites.Sample(columnType, r)).ToArray();
        var sourceSlot = f.Table.GetOrCreateSlot(f.SlotOf("col.src"));
        for (var r = 0; r < Physical; r++)
        {
            ColumnValues.Set(sourceSlot, r, source[r]);
        }

        var destination = f.Table.GetOrCreateSlot(f.SlotOf("col.dst"));
        var expected = new ScalarValue[Physical];
        if (shape == "in place")
        {
            for (var r = 0; r < Physical; r += 3)
            {
                expected[r] = TableWrites.Sample(columnType, r + 500);
                ColumnValues.Set(destination, r, expected[r]);
            }
        }

        // Identity: all but every fifth row, shuffled (rows * 2 >= physical). Gather: a few rows across word boundaries.
        var rows = domain == "identity"
            ? Enumerable.Range(0, Physical).Where(r => r % 5 != 3).OrderBy(r => (r * 7919) % 131).ToArray()
            : new[] { 129, 2, 64, 63, 7, 70 };
        var expression = f.Compile(shape switch
        {
            "column" => "col.src",
            "broadcast" => "param.v",
            _ => "col.dst ?? param.v",
        });

        expression.Evaluate(f.Context, rows, destination);

        foreach (var r in rows)
        {
            expected[r] = shape switch
            {
                "column" => source[r],
                "broadcast" => broadcast,
                _ => expected[r].IsMissing ? broadcast : expected[r],
            };
        }

        TableWrites.AssertRows(destination, expected, $"{type} {domain} {shape}");
        Assert.Equal(expected.Count(v => v.IsMissing), f.Table.CountMissingLiveRows(f.SlotOf("col.dst")));
    }
}
