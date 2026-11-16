using StrataRec.Core.Bindings;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;

namespace StrataRec.Features.Connectors;

/// <summary>The keys of one fetch (design 6.5). Candidate keys are deduplicated.</summary>
/// <remarks>
/// A struct: each <c>max_batch</c> chunk of a fetch is one connector call and a request makes hundreds of them, so an
/// object per call was a measurable share of the request's allocation. Pass it on by <c>in</c> where the signature
/// allows (async methods cannot take <c>in</c> parameters); a copy is five words.
/// </remarks>
public readonly struct EntityKeyBatch
{
    public required KeyShape Shape { get; init; }

    /// <summary>Context key for <see cref="KeyShape.Single"/> and <see cref="KeyShape.Cross"/>.</summary>
    public EntityKey ContextKey { get; init; }

    /// <summary>Candidate keys for <see cref="KeyShape.Many"/> and <see cref="KeyShape.Cross"/>.</summary>
    /// <remarks>
    /// The keys live in the request's pooled buffers (design 4.8): they stay valid until the connector call completes,
    /// even past the request's own timeout, and not after. A connector that needs keys later must copy them.
    /// </remarks>
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
/// (design 6.5).
/// </summary>
/// <remarks>
/// <para>Validity is tracked per field and key, and every (field, key) starts as missing: only values the connector
/// actually wrote are ever read. A key absent from the response needs no call. Values written through
/// <see cref="GetValues{T}"/> count once <see cref="SetValid"/> is called for that field and key; call it after the
/// value is fully decoded, so a record that fails half-way never exposes an unwritten (pooled) value.
/// <see cref="SetString(int, int, string)"/> (and its span and UTF-8 forms) and the list setters (<see cref="SetInt64List"/> and its siblings) mark their value present
/// themselves. A connector reporting <see cref="FetchOutcome.Partial"/> therefore needs no extra bookkeeping.</para>
/// <para>A writer belongs to one connector call while that call runs (each <c>max_batch</c> chunk gets its own), so
/// implementations may write from any one thread at a time without synchronization. The chunks of one fetch share
/// buffers: the memory <see cref="GetValues{T}"/> returns is this call's window of a fetch-wide array, so write only
/// inside it. Once a call has completed, the engine may hand the same writer object to another chunk: a connector must
/// not keep it, nor write through it after returning (see <see cref="IFeatureSourceConnector.FetchAsync"/>).</para>
/// </remarks>
public interface IFeatureBatchWriter
{
    int KeyCount { get; }

    /// <summary>
    /// Native value buffer of a fixed-size field, indexed by key: <c>bool</c>, <c>int</c> (int32), <c>long</c>,
    /// <c>float</c> (float32 and vectors, length KeyCount × dimension) or <c>double</c>. Writes become visible only
    /// through <see cref="SetValid"/>.
    /// </summary>
    Memory<T> GetValues<T>(int fieldIndex)
        where T : unmanaged;

    /// <summary>Marks the value of one field of one key, written through <see cref="GetValues{T}"/>, as present.</summary>
    void SetValid(int fieldIndex, int keyIndex);

    /// <summary>Marks one field of one key as absent (withdrawing a value set earlier, if any).</summary>
    void SetMissing(int fieldIndex, int keyIndex);

    /// <summary>Writes a <c>string</c> or <c>text</c> field value.</summary>
    void SetString(int fieldIndex, int keyIndex, string value);

    /// <summary>
    /// Writes a <c>string</c> or <c>text</c> field value from characters a connector decoded into a buffer of its own.
    /// A <c>string</c> field is categorical, so its values repeat across keys: the writer keeps one string per distinct
    /// value instead of one per key (5,000 keys of a few categories make a few strings, not 5,000). A <c>text</c> value
    /// is a string of its own.
    /// </summary>
    void SetString(int fieldIndex, int keyIndex, ReadOnlySpan<char> value);

    /// <summary>
    /// Writes a <c>string</c> or <c>text</c> field value from its UTF-8 bytes, as most stores return them, without a
    /// string per key for a repeated value (as <see cref="SetString(int, int, ReadOnlySpan{char})"/>). Invalid UTF-8 is
    /// replaced by U+FFFD, as <see cref="System.Text.Encoding.UTF8"/> decodes it.
    /// </summary>
    void SetStringUtf8(int fieldIndex, int keyIndex, ReadOnlySpan<byte> utf8);

    /// <summary>Writes a <c>list&lt;bool&gt;</c> field value.</summary>
    /// <remarks>One setter per element type, named like the field's declared element type: the field's type decides the
    /// width, so a connector decoding int32 ids (tags, topics, token ids) or float32 weights writes them as they are,
    /// with no widened copy. A setter for another element type than the field's is a connector bug and throws.</remarks>
    void SetBoolList(int fieldIndex, int keyIndex, ReadOnlySpan<bool> value);

    /// <summary>Writes a <c>list&lt;int32&gt;</c> field value.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    void SetInt32List(int fieldIndex, int keyIndex, ReadOnlySpan<int> value);

    /// <summary>Writes a <c>list&lt;int64&gt;</c> field value.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    void SetInt64List(int fieldIndex, int keyIndex, ReadOnlySpan<long> value);

    /// <summary>Writes a <c>list&lt;float32&gt;</c> field value.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    void SetFloat32List(int fieldIndex, int keyIndex, ReadOnlySpan<float> value);

    /// <summary>Writes a <c>list&lt;float64&gt;</c> field value.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
    void SetFloat64List(int fieldIndex, int keyIndex, ReadOnlySpan<double> value);

    /// <summary>Writes a <c>list&lt;string&gt;</c> field value; no element may be null.</summary>
    /// <inheritdoc cref="SetBoolList" path="/remarks"/>
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
    /// <remarks>
    /// <paramref name="fields"/> holds only the fields the request needs at this point (late materialization,
    /// design 6.8), so the same source may be called again later for other fields of the same keys. The engine stops
    /// waiting at the source timeout or the request deadline, whichever comes first, and discards whatever the call
    /// writes afterwards; the writer's buffers stay rented until the call actually completes (design 4.8). The same
    /// holds for <see cref="EntityKeyBatch.CandidateKeys"/>: neither may be kept past completion.
    /// </remarks>
    ValueTask<FetchStatus> FetchAsync(
        EntityKeyBatch keys,
        ReadOnlyMemory<CompiledFieldBinding> fields,
        IFeatureBatchWriter writer,
        CancellationToken cancellationToken);
}

/// <summary>Creates connectors for bindings of one connector type.</summary>
/// <remarks>
/// The engine creates one connector per binding identity and shares it across plans and their concurrent requests (see
/// <see cref="Bindings.BindingSpec.Identity"/>), so a connector lives as long as some plan uses it, not one plan.
/// <see cref="Create"/> only reads the settings; a connector that must connect or resolve credentials first implements
/// <see cref="ILoadable"/>, and one holding resources implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>:
/// the engine disposes it once nothing can call it any more. The whole lifetime contract is on
/// <see cref="BindingContext"/>.
/// </remarks>
public interface IFeatureConnectorFactory
{
    string ConnectorType { get; }

    /// <summary>
    /// The C# type the connector settings bind to (<see cref="Artifacts.Model.BindingSettings"/>): the single source of
    /// their JSON Schema (design 9.1). <see cref="FeatureConnectorFactory{TSettings}"/> binds them to it before its
    /// <c>Create</c> sees them; <see cref="NoSettings"/> for a connector that takes none.
    /// </summary>
    Type SettingsType { get; }

    /// <param name="context">The source's logical name (e.g. <c>item_store</c>), its connector settings, the base directory
    /// relative files resolve against and the host's secret store.</param>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    IFeatureSourceConnector Create(BindingContext context);
}

/// <summary>
/// The base of a connector factory: it declares the type of its settings once, as its type argument, and gets them bound
/// to it, so the type the schema describes is the type that binds, and the factory parses no JSON.
/// </summary>
/// <typeparam name="TSettings">The settings type: snake_case members, documented for the schema, required ones
/// <c>required</c>; <see cref="LocalFile"/> and <see cref="SecretReference"/> for files and credentials.</typeparam>
public abstract class FeatureConnectorFactory<TSettings> : IFeatureConnectorFactory
    where TSettings : class
{
    public abstract string ConnectorType { get; }

    public Type SettingsType => typeof(TSettings);

    /// <summary>Binds the settings strictly (<see cref="Artifacts.Model.BindingSettings"/>) and creates the connector from them.</summary>
    /// <exception cref="BindingException">The settings are wrong: at the setting's pointer, with a hint.</exception>
    public IFeatureSourceConnector Create(BindingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Create(Artifacts.Model.BindingSettings.Bind<TSettings>(context, ConnectorType), context);
    }

    /// <summary>Creates the connector: checks what the settings type cannot say, and reads nothing else (see <see cref="BindingContext"/>).</summary>
    /// <exception cref="BindingException">A setting is wrong: at its pointer, with a hint.</exception>
    protected abstract IFeatureSourceConnector Create(TSettings settings, BindingContext context);
}
