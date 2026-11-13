using System.Text.Json;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Connectors;

/// <summary>The keys of one fetch (design 6.5). Candidate keys are deduplicated.</summary>
public sealed class EntityKeyBatch
{
    public required KeyShape Shape { get; init; }

    /// <summary>Context key for <see cref="KeyShape.Single"/>, <see cref="KeyShape.Cross"/> and <see cref="KeyShape.ContextMap"/>.</summary>
    public EntityKey ContextKey { get; init; }

    /// <summary>Candidate keys for <see cref="KeyShape.Many"/> and <see cref="KeyShape.Cross"/>.</summary>
    public ReadOnlyMemory<EntityKey> CandidateKeys { get; init; }

    /// <summary>Number of staging rows: 1 for single-key shapes, otherwise the candidate key count.</summary>
    public int KeyCount => Shape is KeyShape.Single ? 1 : CandidateKeys.Length;
}

/// <summary>A physical field requested from a source and where its values go in the staging area.</summary>
/// <param name="Field">Physical field name in the store.</param>
/// <param name="FieldIndex">Index used with <see cref="IFeatureBatchWriter"/>.</param>
/// <param name="Type">Storage type the connector must write.</param>
public readonly record struct CompiledFieldBinding(string Field, int FieldIndex, ColumnType Type);

/// <summary>
/// Dense staging area the connector decodes into, ordered by the batch's keys and independent of table rows
/// (design 6.5). Every key starts as "not found"; connectors call <see cref="SetFound"/> for keys present in the
/// response and <see cref="SetMissing"/> for individual absent fields.
/// </summary>
public interface IFeatureBatchWriter
{
    int KeyCount { get; }

    /// <summary>
    /// Native value buffer of a fixed-size field, indexed by key: <c>bool</c>, <c>int</c> (int32), <c>long</c>,
    /// <c>float</c> (float32 and vectors, length KeyCount × dimension) or <c>double</c>.
    /// </summary>
    Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged;

    /// <summary>Marks a key as present in the response (all of its fields hold values unless set missing).</summary>
    void SetFound(int keyIndex);

    /// <summary>Marks one field of a found key as absent.</summary>
    void SetMissing(int fieldIndex, int keyIndex);

    /// <summary>Writes a <c>string</c> or <c>text</c> field value.</summary>
    void SetString(int fieldIndex, int keyIndex, string value);

    void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value);

    void SetStringList(int fieldIndex, int keyIndex, ReadOnlySpan<string> value);
}

public enum FetchOutcome : byte
{
    Ok = 1,

    /// <summary>Some keys failed (e.g. one of several batched commands timed out); readers become Degraded.</summary>
    Partial,

    /// <summary>The source failed; nodes reading its features fail (design 6.7, layer 2).</summary>
    Failed,
}

public readonly record struct FetchStatus(FetchOutcome Outcome, int FailedKeys = 0, string? Error = null)
{
    public static FetchStatus Ok => new(FetchOutcome.Ok);

    public static FetchStatus Failed(string error) => new(FetchOutcome.Failed, 0, error);
}

/// <summary>A storage-specific feature fetcher (design 6.5). Implementations must be thread-safe.</summary>
public interface IFeatureSourceConnector
{
    /// <summary>Connector type and version, e.g. <c>redis_hash@1</c>.</summary>
    string ConnectorType { get; }

    bool Supports(KeyShape shape);

    /// <summary>
    /// Fetches <paramref name="fields"/> for <paramref name="keys"/> and decodes them straight into
    /// <paramref name="writer"/>. Must not write after it returns or after cancellation is acknowledged.
    /// </summary>
    ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys,
        ReadOnlyMemory<CompiledFieldBinding> fields,
        IFeatureBatchWriter writer,
        CancellationToken cancellationToken);
}

/// <summary>Creates connectors for bindings of one connector type.</summary>
public interface IFeatureConnectorFactory
{
    string ConnectorType { get; }

    /// <param name="sourceName">Logical source name, e.g. <c>item_store</c>.</param>
    /// <param name="settings">Connector-specific binding settings.</param>
    /// <param name="baseDirectory">Directory relative file references resolve against (the config root), if any.</param>
    IFeatureSourceConnector Create(string sourceName, IReadOnlyDictionary<string, JsonElement> settings, string? baseDirectory);
}
