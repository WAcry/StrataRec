using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.IntegrationTests;

internal static class LogColumnRows
{
    public static ScalarValue[] Rows(this LogColumn column) => Enumerable.Range(0, column.RowCount).Select(column.Get).ToArray();
}
