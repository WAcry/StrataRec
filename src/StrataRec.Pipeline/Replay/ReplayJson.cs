using System.Buffers;
using System.Globalization;
using System.Text.Json;
using StrataRec.Artifacts.Model;
using StrataRec.Core.Diagnostics;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Logging;

namespace StrataRec.Pipeline.Replay;

/// <summary>A replay bundle that cannot be used: where it is wrong (a JSON pointer into the bundle) and how to fix it.</summary>
public sealed class ReplayBundleException : Exception
{
    public ReplayBundleException(string pointer, string message, string? hint = null)
        : base(pointer.Length == 0 ? message : $"{pointer}: {message}")
    {
        Pointer = pointer;
        Reason = message;
        Hint = hint;
    }

    /// <summary>RFC 6901 pointer into the bundle.</summary>
    public string Pointer { get; }

    public string Reason { get; }

    public string? Hint { get; }
}

/// <summary>Spellings used in bundles for node attempt timings (<see cref="ReplayAttemptRecord.Timing"/>).</summary>
public static class ReplayTiming
{
    /// <summary>The attempt started and finished (successfully or not) within its budget.</summary>
    public const string Ran = "ran";

    /// <summary>No budget was left to start the attempt.</summary>
    public const string DeadlineExceeded = "deadline_exceeded";

    /// <summary>The attempt was cancelled when its budget ran out.</summary>
    public const string TimedOut = "timed_out";
}

/// <summary>
/// Converts between engine values and the JSON of a bundle. Every value goes through the log encoding
/// (<see cref="LogBatchJson.WriteValue"/> / <see cref="LogBatchJson.TryReadValue"/>), so bundles and logs share one value
/// encoding, with one difference: a bundle keeps NaN and ±∞ (as the strings <c>"NaN"</c>, <c>"Infinity"</c>,
/// <c>"-Infinity"</c>) where a log writes null, because a replay must see what the request saw. A value is always read
/// with the type recorded next to it. Readers throw <see cref="ReplayBundleException"/> with the pointer of the bad value.
/// </summary>
public static class ReplayJson
{
    /// <summary>The type spelling of a value's kind, for a value without a declared type (e.g. an undeclared input).</summary>
    public static ColumnType TypeOf(in ScalarValue value) => value.Kind switch
    {
        StorageKind.Bool => ColumnType.Bool,
        StorageKind.Int64 => ColumnType.Int64,
        StorageKind.Float64 => ColumnType.Float64,
        StorageKind.Text => ColumnType.Text,
        StorageKind.Vector => ColumnType.Vector(value.AsVector().Length),
        StorageKind.ListInt64 => ColumnType.ListInt64,
        StorageKind.ListString => ColumnType.ListString,
        _ => ColumnType.String,
    };

    public static JsonElement Value(in ScalarValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            LogBatchJson.WriteValue(json, value, nonFiniteAsStrings: true);
        }

        return Parse(buffer);
    }

    public static JsonElement Values(ReadOnlySpan<ScalarValue> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var value in values)
            {
                LogBatchJson.WriteValue(json, value, nonFiniteAsStrings: true);
            }

            json.WriteEndArray();
        }

        return Parse(buffer);
    }

    public static JsonElement Key(in EntityKey key)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            WriteKey(json, key);
        }

        return Parse(buffer);
    }

    public static JsonElement Keys(ReadOnlySpan<EntityKey> keys)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var key in keys)
            {
                WriteKey(json, key);
            }

            json.WriteEndArray();
        }

        return Parse(buffer);
    }

    /// <summary>float32 values; a non-finite value is written as its name (<c>"NaN"</c>, <c>"Infinity"</c>, <c>"-Infinity"</c>).</summary>
    public static JsonElement Floats(ReadOnlySpan<float> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var value in values)
            {
                LogBatchJson.WriteValue(json, ScalarValue.FromFloat64(value), nonFiniteAsStrings: true);
            }

            json.WriteEndArray();
        }

        return Parse(buffer);
    }

    public static ReplayTypedValue Typed(ColumnType type, in ScalarValue value) =>
        new() { Type = type.ToString(), Value = value.IsMissing ? null : Value(value) };

    public static ColumnType Type(string? text, string pointer)
    {
        if (!ColumnType.TryParse(text, out var type, out var error))
        {
            throw new ReplayBundleException(pointer, error, "use a type spelling such as \"float32\", \"int64?\" or \"vec<float32,4>\"");
        }

        return type;
    }

    public static ScalarValue Value(JsonElement? json, ColumnType type, string pointer)
    {
        if (json is not { } element)
        {
            return ScalarValue.Missing;
        }

        if (!LogBatchJson.TryReadValue(element, type, out var value, out var error, nonFiniteAsStrings: true))
        {
            throw new ReplayBundleException(pointer, error!,
                $"values use the log encoding of their recorded type ({type}), with \"NaN\", \"Infinity\" and \"-Infinity\" for non-finite floats");
        }

        return value;
    }

    /// <summary>A value array aligned with <paramref name="expected"/> rows.</summary>
    public static ScalarValue[] Values(JsonElement json, ColumnType type, int expected, string pointer)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer, $"expected an array of {type} values, got {json.ValueKind}");
        }

        if (json.GetArrayLength() != expected)
        {
            throw new ReplayBundleException(pointer, $"expected {expected} values (one per key), got {json.GetArrayLength()}",
                "values are aligned with the call's keys");
        }

        var values = new ScalarValue[expected];
        var i = 0;
        foreach (var element in json.EnumerateArray())
        {
            values[i] = Value(element, type, pointer + "/" + i.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        return values;
    }

    public static EntityKey Key(JsonElement json, string pointer) => json.ValueKind switch
    {
        JsonValueKind.Number when json.TryGetInt64(out var id) => EntityKey.FromInt64(id),
        JsonValueKind.String => EntityKey.FromString(json.GetString()!),
        _ => throw new ReplayBundleException(pointer, $"an entity key must be an int64 number or a string, got {json.GetRawText()}"),
    };

    public static EntityKey[] Keys(JsonElement? json, string pointer)
    {
        if (json is not { } array)
        {
            return Array.Empty<EntityKey>();
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer, $"expected an array of entity keys, got {array.ValueKind}");
        }

        var keys = new EntityKey[array.GetArrayLength()];
        var i = 0;
        foreach (var element in array.EnumerateArray())
        {
            keys[i] = Key(element, pointer + "/" + i.ToString(CultureInfo.InvariantCulture));
            i++;
        }

        return keys;
    }

    /// <summary>float32 values, with non-finite values written as their names.</summary>
    public static float[] Floats(JsonElement json, string pointer)
    {
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new ReplayBundleException(pointer, $"expected an array of numbers, got {json.ValueKind}");
        }

        var values = new float[json.GetArrayLength()];
        var i = 0;
        foreach (var element in json.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                values[i] = element.GetSingle();
            }
            else if (LogBatchJson.TryNonFinite(element, out var named))
            {
                values[i] = (float)named;
            }
            else
            {
                throw new ReplayBundleException(pointer + "/" + i.ToString(CultureInfo.InvariantCulture), $"expected a number, got {element.GetRawText()}",
                    "write NaN and infinities as \"NaN\", \"Infinity\" or \"-Infinity\"");
            }

            i++;
        }

        return values;
    }

    /// <summary>The status of a call that was still in flight when its request was aborted (design 13.4).</summary>
    public const string Outstanding = "outstanding";

    /// <summary>The error recorded with an <see cref="Outstanding"/> call.</summary>
    public const string OutstandingError = "still in flight when the request was aborted";

    /// <summary>
    /// A recorded status. An outstanding call never answered the request, so it replays like a cancelled one: the request
    /// stopped waiting for it.
    /// </summary>
    public static IoOutcome Outcome(string? status, string pointer) => status switch
    {
        "ok" => IoOutcome.Ok,
        "partial" => IoOutcome.Partial,
        "timeout" => IoOutcome.Timeout,
        "error" => IoOutcome.Error,
        "cancelled" or Outstanding => IoOutcome.Cancelled,
        _ => throw new ReplayBundleException(pointer, $"unknown status '{status}'", "use ok, partial, timeout, error, cancelled or outstanding"),
    };

    public static string ShapeName(KeyShape shape) => shape switch
    {
        KeyShape.Single => "single",
        KeyShape.Many => "many",
        KeyShape.Cross => "cross",
        _ => "context_map",
    };

    public static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    private static void WriteKey(Utf8JsonWriter json, in EntityKey key)
    {
        if (key.StringValue is { } s)
        {
            json.WriteStringValue(s);
        }
        else
        {
            json.WriteNumberValue(key.Int64Value);
        }
    }

    private static JsonElement Parse(ArrayBufferWriter<byte> buffer)
    {
        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }
}
