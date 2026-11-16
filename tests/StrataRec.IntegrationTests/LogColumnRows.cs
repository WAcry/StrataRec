using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.IntegrationTests;

internal static class LogColumnRows
{
    /// <summary>A log column's rows as values (<see cref="LogColumn.Get"/>), for assertions.</summary>
    public static ScalarValue[] Rows(this LogColumn column) => Enumerable.Range(0, column.RowCount).Select(column.Get).ToArray();
}
