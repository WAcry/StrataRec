using StrataRec.Core.Memory;
using StrataRec.Core.Services;
using StrataRec.Core.Types;
using StrataRec.Core.Values;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// Request-level values as a service call's inputs: what an operator sends from expressions over <c>ctx.*</c> and
/// <c>param.*</c> (a recall's query; a filter's user segment). One conversion, so every operator sends a
/// value the same way and a recording digests it the same way.
/// </summary>
public static class ServiceInputs
{
    /// <summary>
    /// <paramref name="value"/>, of <paramref name="type"/>, as the input <paramref name="name"/>: a scalar as a tensor of
    /// shape <c>[]</c>, a list as one of shape <c>[n]</c>, each element at the width of the type's element kind (a
    /// <c>float32</c> as a float, exactly: it was checked against its type where it entered). A missing value
    /// is sent as missing (<see cref="NamedInput.IsMissing"/>), with the type's element type and shape.
    /// </summary>
    /// <param name="memory">The request's memory, which the input's elements are rented from.</param>
    public static NamedInput Create(string name, ColumnType type, in ScalarValue value, RequestMemory memory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(memory);
        var list = type.Kind == StorageKind.List;
        if (value.IsMissing)
        {
            return NamedInput.FromMissing(name, ElementType(list ? type.Element : type.Kind), list ? [type.FixedLength > 0 ? type.FixedLength : -1] : []);
        }

        if (!list)
        {
            return type.Kind switch
            {
                StorageKind.Bool => Scalar(name, memory, value.AsBool()),
                StorageKind.Int32 => Scalar(name, memory, value.AsInt32()),
                StorageKind.Int64 => Scalar(name, memory, value.AsInt64()),
                StorageKind.Float32 => Scalar(name, memory, value.AsFloat32()),
                StorageKind.Float64 => Scalar(name, memory, value.AsFloat64()),
                _ => Scalar(name, memory, value.AsString()),
            };
        }

        int[] shape = [value.ListLength];
        return type.Element switch
        {
            StorageKind.Bool => NamedInput.FromValues(name, shape, Copy(memory, value.AsBoolList())),
            StorageKind.Int32 => NamedInput.FromValues(name, shape, Narrow(memory, value.AsInt64List(), static l => (int)l)),
            StorageKind.Int64 => NamedInput.FromValues(name, shape, Copy(memory, value.AsInt64List())),
            StorageKind.Float32 => NamedInput.FromValues(name, shape, Narrow(memory, value.AsFloat64List(), static d => (float)d)),
            StorageKind.Float64 => NamedInput.FromValues(name, shape, Copy(memory, value.AsFloat64List())),
            _ => NamedInput.FromValues(name, shape, Copy(memory, value.AsStringList())),
        };
    }

    /// <summary>The tensor element type of values of <paramref name="kind"/>.</summary>
    public static TensorElementType ElementType(StorageKind kind) => kind switch
    {
        StorageKind.Bool => TensorElementType.Bool,
        StorageKind.Int32 => TensorElementType.Int32,
        StorageKind.Int64 => TensorElementType.Int64,
        StorageKind.Float32 => TensorElementType.Float32,
        StorageKind.Float64 => TensorElementType.Float64,
        StorageKind.String => TensorElementType.String,
        _ => throw new ArgumentException($"{kind} has no tensor element type.", nameof(kind)),
    };

    private static NamedInput Scalar<T>(string name, RequestMemory memory, T value)
    {
        var values = memory.Rent<T>(1);
        values[0] = value;
        return NamedInput.FromValues(name, [], new ReadOnlyMemory<T>(values, 0, 1));
    }

    private static ReadOnlyMemory<T> Copy<T>(RequestMemory memory, ReadOnlySpan<T> list)
    {
        var values = memory.Rent<T>(Math.Max(1, list.Length));
        list.CopyTo(values);
        return new ReadOnlyMemory<T>(values, 0, list.Length);
    }

    private static ReadOnlyMemory<TTo> Narrow<TFrom, TTo>(RequestMemory memory, ReadOnlySpan<TFrom> list, Func<TFrom, TTo> convert)
    {
        var values = memory.Rent<TTo>(Math.Max(1, list.Length));
        for (var i = 0; i < list.Length; i++)
        {
            values[i] = convert(list[i]);
        }

        return new ReadOnlyMemory<TTo>(values, 0, list.Length);
    }
}
