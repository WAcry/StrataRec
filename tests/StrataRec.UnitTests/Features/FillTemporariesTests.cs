using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Features.Runtime;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <see cref="StrataRec.Core.Memory.RequestMemory.RentedBytes"/> counts the ledger only.
/// </summary>
public class FillTemporariesTests
{
    [Fact]
    public async Task AFillOrPrefetchWithNothingLeftToDo_PutsNothingOnTheLedger()
    {
        var rig = new FeatureTestRig().Source("item_store", ScriptedConnector.Values((key, _) => key));
        var @base = rig.Physical("item.base", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(0));
        var copy = rig.Derived("item.copy", FeatureTestRig.Item, ColumnType.Int64.AsNullable(), TestExpression.CopyInt64("item.base", rig.SlotOf(@base)), [@base]);
        rig.Build();
        var request = rig.NewRequest([1, 2, 3]);
        await request.EnsureAsync(copy);
        var ledger = request.Memory.RentedBytes;

        await request.EnsureAsync(copy);
        await request.Fetcher.EnsureAsync([copy], [2, 0], CancellationToken.None);
        request.Fetcher.Prefetch([@base], CancellationToken.None);

        Assert.Equal(ledger, request.Memory.RentedBytes);
    }

    [Fact]
    public async Task ASecondLocalReadOfAsManyRows_PutsNothingMoreOnTheLedger()
    {
        const int Rows = 1_000;
        var rig = new FeatureTestRig().LocalSource("item_store", writer =>
        {
            for (var id = 1; id <= 2 * Rows; id++)
            {
                var record = writer.Begin(id, 1);
                record.Set(writer.Schema.FieldIndex("score"), ScalarValue.FromFloat64(id * 0.5));
                record.Set(writer.Schema.FieldIndex("clicks"), ScalarValue.FromInt64(id));
                Assert.True(record.Commit());
            }
        });
        var score = rig.Physical("item.score", FeatureTestRig.Item, ColumnType.Float64, "item_store");
        var clicks = rig.Physical("item.clicks", FeatureTestRig.Item, ColumnType.Int64, "item_store", ScalarValue.FromInt64(0));
        rig.Build();
        var request = rig.NewRequest(Enumerable.Range(1, 2 * Rows).Select(i => (long)i).ToArray());
        await request.Fetcher.EnsureAsync([score, clicks], Enumerable.Range(0, Rows).ToArray(), CancellationToken.None);
        var ledger = request.Memory.RentedBytes;

        await request.Fetcher.EnsureAsync([score, clicks], Enumerable.Range(Rows, Rows).ToArray(), CancellationToken.None);

        Assert.Equal(ledger, request.Memory.RentedBytes);
        Assert.Equal(2 * Rows * 0.5, request.Value(score, (2 * Rows) - 1).AsFloat64());
        Assert.Equal(Rows + 1, request.Value(clicks, Rows).AsInt64());
    }
}
