using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <remarks>A buffer made directly counts every row valid, so a writer that writes values in bulk and forgets to mark
/// them passes there and leaves table rows missing.</remarks>
internal static class TableWrites
{
    public static readonly string[] EveryStorageType = Enum.GetValues<StorageKind>().SelectMany(kind => kind switch
    {
        StorageKind.List => new[]
        {
            "list<bool>", "list<int32>", "list<int64>", "list<float32>", "list<float64>", "list<string>",
            "list<float32,3>", "list<int32,3>", "list<bool,3>", "list<string,2>",
        },
        _ => new[] { Scalar(kind) },
    }).ToArray();

    private static string Scalar(StorageKind kind) => kind switch
    {
        StorageKind.Bool => "bool",
        StorageKind.Int32 => "int32",
        StorageKind.Int64 => "int64",
        StorageKind.Float32 => "float32",
        StorageKind.Float64 => "float64",
        StorageKind.String => "string",
        _ => throw new NotSupportedException($"Add a column type of storage kind {kind} to the write contract tests."),
    };

    public static ScalarValue Sample(ColumnType type, int n) => type.Kind switch
    {
        StorageKind.Bool => ScalarValue.FromBool(n % 2 == 0),
        StorageKind.Int32 or StorageKind.Int64 => ScalarValue.FromInt64((3L * n) + 1),
        StorageKind.Float32 or StorageKind.Float64 => ScalarValue.FromFloat64(n + 0.5),
        StorageKind.String => ScalarValue.FromString("s" + n),
        StorageKind.List => type.Element switch
        {
            StorageKind.Bool => ScalarValue.FromBoolList(new[] { n % 2 == 0, true, false }),
            StorageKind.Int32 or StorageKind.Int64 => ScalarValue.FromInt64List(new long[] { n, -n, 7 }),
            StorageKind.Float32 or StorageKind.Float64 => ScalarValue.FromFloat64List(new[] { n + 0.5, -0.25, 7 }),
            _ => ScalarValue.FromStringList(new[] { "a" + n, "b" }),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static void AssertRows(ColumnBuffer slot, IReadOnlyList<ScalarValue> expected, string label)
    {
        for (var row = 0; row < expected.Count; row++)
        {
            var actual = ColumnValues.Get(slot, row);
            Assert.True(slot.IsValid(row) == !expected[row].IsMissing && actual.Equals(expected[row]),
                $"{label}, row {row}: expected {Describe(expected[row])}, found {Describe(actual)}");
        }
    }

    private static string Describe(in ScalarValue value) => value.IsMissing ? "missing" : value.ToString();
}

internal sealed class DirtyPool : IBufferPool
{
    public T[] Rent<T>(int minimumLength)
    {
        var array = new T[Math.Max(1, minimumLength) + 3];
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            MemoryMarshal.CreateSpan(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(array)), array.Length * Unsafe.SizeOf<T>()).Fill(0xA5);
        }

        return array;
    }

    public void Return<T>(T[] array, bool clearArray)
    {
    }
}
