using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StrataRec.Core.Columns;
using StrataRec.Core.Memory;
using StrataRec.Core.Tables;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.UnitTests.Core;

/// <summary>
/// The write contract of a table slot (<see cref="CandidateTable"/>): a slot starts with every row missing, and after a
/// writer ran, exactly the rows it wrote a value into are present, holding that value; every other row is still missing.
/// </summary>
/// <remarks>Test writers against real table slots with this helper. A buffer made directly counts every row valid, so a
/// writer that writes values in bulk and forgets to mark them passes there and leaves table rows missing (review 13).</remarks>
internal static class TableWrites
{
    /// <summary>One non-nullable column type of each <see cref="StorageKind"/>; a new kind fails here until it has one.</summary>
    public static readonly string[] EveryStorageType = Enum.GetValues<StorageKind>().Select(kind => kind switch
    {
        StorageKind.Bool => "bool",
        StorageKind.Int32 => "int32",
        StorageKind.Int64 => "int64",
        StorageKind.Float32 => "float32",
        StorageKind.Float64 => "float64",
        StorageKind.String => "string",
        StorageKind.Text => "text",
        StorageKind.Vector => "vec<float32,3>",
        StorageKind.ListInt32 => "list<int32>",
        StorageKind.ListInt64 => "list<int64>",
        StorageKind.ListString => "list<string>",
        _ => throw new NotSupportedException($"Add a column type of storage kind {kind} to the write contract tests."),
    }).ToArray();

    /// <summary>A present value of the type, different for each <paramref name="n"/> (bool alternates), stored exactly.</summary>
    public static ScalarValue Sample(ColumnType type, int n) => type.Kind switch
    {
        StorageKind.Bool => ScalarValue.FromBool(n % 2 == 0),
        StorageKind.Int32 or StorageKind.Int64 => ScalarValue.FromInt64((3L * n) + 1),
        StorageKind.Float32 or StorageKind.Float64 => ScalarValue.FromFloat64(n + 0.5),
        StorageKind.String => ScalarValue.FromString("s" + n),
        StorageKind.Text => ScalarValue.FromText("t" + n),
        StorageKind.Vector => ScalarValue.FromVector(Enumerable.Range(0, type.Dimension).Select(i => (float)(n + i)).ToArray()),
        StorageKind.ListInt32 or StorageKind.ListInt64 => ScalarValue.FromInt64List(new long[] { n, -n, 7 }),
        StorageKind.ListString => ScalarValue.FromStringList(new[] { "a" + n, "b" }),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>
    /// Asserts every physical row of <paramref name="slot"/>: present with the value in <paramref name="expected"/>, or
    /// missing where that is missing (written as missing, or never written).
    /// </summary>
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

/// <summary>Hands out new arrays filled with 0xA5 bytes (references stay null), like a pool holding an earlier request's data.</summary>
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
