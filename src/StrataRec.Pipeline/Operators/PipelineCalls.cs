using System.Collections.Immutable;
using StrataRec.Core.Keys;
using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Pipeline.Execution;
using StrataRec.Pipeline.Planning;

namespace StrataRec.Pipeline.Operators;

/// <summary>An output column or an output value of a pipeline, as a caller sees it.</summary>
public readonly record struct PipelineOutput(string Name, ColumnType Type);

/// <summary>
/// What a pipeline offers the operator of another that calls it: the inputs it declares, the columns and values its
/// output returns, and its key type. An experiment cannot change any of it (<c>request_schema</c> and <c>output</c> are
/// not patchable, declarations are add-only), so a caller is checked against it once, not per arm combination.
/// </summary>
public sealed class PipelineInterface
{
    public required string PipelineId { get; init; }

    public required EntityKeyType KeyType { get; init; }

    /// <summary>The inputs it declares (<c>request_schema</c>).</summary>
    public required RequestSchema Inputs { get; init; }

    /// <summary>Its output columns in order, with the types its results carry (<see cref="ResultColumns.Types"/>).</summary>
    public required ImmutableArray<PipelineOutput> Columns { get; init; }

    /// <summary>Its output's request-level values in order (<c>output.values</c>).</summary>
    public ImmutableArray<PipelineOutput> Values { get; init; } = [];

    /// <summary>The pipelines its own nodes call, in any arm, sorted: what a cycle is found with.</summary>
    public ImmutableArray<string> Calls { get; init; } = [];

    /// <summary>The interface of a compiled plan.</summary>
    public static PipelineInterface Of(ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var columns = ImmutableArray.CreateBuilder<PipelineOutput>(plan.Output.Columns.Length);
        foreach (var (name, expression) in plan.Output.Columns)
        {
            columns.Add(new PipelineOutput(name, plan.Schema.DeclaredType(expression)));
        }

        var values = ImmutableArray.CreateBuilder<PipelineOutput>(plan.Output.Values.Length);
        foreach (var (name, expression) in plan.Output.Values)
        {
            values.Add(new PipelineOutput(name, plan.Schema.DeclaredType(expression)));
        }

        return new PipelineInterface
        {
            PipelineId = plan.PipelineId,
            KeyType = plan.TableSchema.KeyType,
            Inputs = plan.RequestSchema,
            Columns = columns.MoveToImmutable(),
            Values = values.MoveToImmutable(),
            Calls = plan.Calls,
        };
    }

    public bool TryGetColumn(string name, out ColumnType type) => TryGet(Columns, name, out type);

    public bool TryGetValue(string name, out ColumnType type) => TryGet(Values, name, out type);

    private static bool TryGet(ImmutableArray<PipelineOutput> outputs, string name, out ColumnType type)
    {
        foreach (var output in outputs)
        {
            if (output.Name == name)
            {
                type = output.Type;
                return true;
            }
        }

        type = default;
        return false;
    }
}

/// <summary>What a compile knows of a pipeline one of its nodes calls (<see cref="IPipelineDirectory"/>).</summary>
public enum PipelineLookupStatus
{
    /// <summary>The pipeline and its interface are known: the call is checked now.</summary>
    Known,

    /// <summary>The pipelines the plan may call are known, and this is none of them.</summary>
    Unknown,

    /// <summary>The pipeline is known and does not compile: the lookup's detail is its first error.</summary>
    Failed,

    /// <summary>The pipeline is being compiled for a call that leads back to it: the calls form a cycle.</summary>
    Cycle,
}

/// <summary>The answer of an <see cref="IPipelineDirectory"/>.</summary>
/// <param name="Interface">The pipeline's interface when <see cref="PipelineLookupStatus.Known"/>.</param>
/// <param name="Detail">For <see cref="PipelineLookupStatus.Unknown"/>, a hint (a pipeline of a close name); for
/// <see cref="PipelineLookupStatus.Cycle"/>, the calls from the pipeline back to itself, e.g. <c>a → b → a</c>; for
/// <see cref="PipelineLookupStatus.Failed"/>, the pipeline's first error.</param>
public readonly record struct PipelineLookup(PipelineLookupStatus Status, PipelineInterface? Interface = null, string? Detail = null);

/// <summary>
/// The pipelines a plan's nodes may call, for a compile that knows them: a workspace's (<c>validate</c>, <c>build</c>,
/// <c>run</c>, a host serving a workspace). A compile without one (an engine loading one pipeline's release) checks
/// nothing of a call: the engine checks it against the pipeline it serves when the call is made
/// (<see cref="OperatorContext.CallPipelineAsync"/>).
/// </summary>
public interface IPipelineDirectory
{
    /// <summary>
    /// What is known of <paramref name="pipelineId"/>, asked while compiling <paramref name="caller"/> with
    /// <paramref name="environment"/>: the operators and bindings of the binary, which the called pipeline is compiled
    /// with too.
    /// </summary>
    PipelineLookup Find(string pipelineId, string caller, PlanEnvironment environment);
}

/// <summary>
/// A node's declared call of another pipeline (<see cref="OperatorCompileContext.TryCallPipeline"/>): the handle its
/// operator calls with, and what the call needs of the pipeline's interface, which is checked when the plan is compiled
/// with the pipelines it may call, and otherwise when the call is made.
/// </summary>
public sealed class PipelineCall
{
    private readonly List<PipelineOutput> _inputs = new();
    private readonly List<PipelineOutput> _columns = new();
    private readonly List<PipelineOutput> _values = new();

    /// <summary>The last served plan of the pipeline the call was checked against, with what the check found.</summary>
    private Checked? _checked;

    internal PipelineCall(string pipelineId, string caller, string nodeId, string configKey, RequestSchema callerInputs, EntityKeyType callerKeyType,
        PipelineInterface? known)
    {
        PipelineId = pipelineId;
        Caller = caller;
        NodeId = nodeId;
        ConfigKey = configKey;
        CallerInputs = callerInputs;
        CallerKeyType = callerKeyType;
        Interface = known;
    }

    /// <summary>The pipeline called.</summary>
    public string PipelineId { get; }

    /// <summary>
    /// The pipeline's interface, when the plan was compiled with the pipelines it may call
    /// (<see cref="IPipelineDirectory"/>); null when it was not, as in an engine that loads each pipeline's release on its
    /// own: an operator then reads a result's columns by what the result says it has.
    /// </summary>
    public PipelineInterface? Interface { get; }

    /// <summary>The pipeline of the node that calls.</summary>
    internal string Caller { get; }

    internal string NodeId { get; }

    /// <summary>Where the node's config names the pipeline.</summary>
    internal string ConfigKey { get; }

    internal RequestSchema CallerInputs { get; }

    internal EntityKeyType CallerKeyType { get; }

    /// <summary>The inputs the operator passes itself, with the types it passes them as.</summary>
    internal IReadOnlyList<PipelineOutput> Inputs => _inputs;

    /// <summary>The output columns the operator reads, each with the type it reads it into.</summary>
    internal IReadOnlyList<PipelineOutput> Columns => _columns;

    internal IReadOnlyList<PipelineOutput> Values => _values;

    /// <summary>The operator appends the result's rows: the pipeline's keys must be of the caller's key type.</summary>
    internal bool AppendsRows { get; set; }

    /// <summary>The key type the operator reads the result's ids as (<see cref="OperatorCompileContext.CallReadsKeys"/>); null when it said none.</summary>
    internal EntityKeyType? ReadsKeys { get; set; }

    /// <summary>The node's cache of the call's results (<see cref="OperatorCompileContext.TryCallPipeline"/>); null for a call that always runs the pipeline.</summary>
    internal PipelineCallCache? Cache { get; set; }

    internal bool PassesInput(string name) => IndexOf(_inputs, name) >= 0;

    internal void AddInput(string name, ColumnType type) => _inputs.Add(new PipelineOutput(name, type));

    internal void AddColumn(string name, ColumnType into) => _columns.Add(new PipelineOutput(name, into));

    internal void AddValue(string name, ColumnType into) => _values.Add(new PipelineOutput(name, into));

    /// <summary>
    /// What of <paramref name="callee"/> does not fit the call, or null: the first of its key type, an input the call passes,
    /// an input the pipeline requires and nothing passes, a column or a value the operator reads. In the words a failed
    /// call reports; a compile reports each at the config key that declared it (<see cref="OperatorCompileContext"/>).
    /// </summary>
    internal string? Mismatch(PipelineInterface callee)
    {
        if (AppendsRows && KeyMismatch(callee) is { } key)
        {
            return key;
        }

        if (ReadsKeys is { } keys && ReadKeyMismatch(callee, keys) is { } read)
        {
            return read;
        }

        foreach (var input in _inputs)
        {
            if (InputMismatch(callee, input.Name, input.Type) is { } error)
            {
                return error;
            }
        }

        if (MissingInput(callee) is { } missing)
        {
            return missing;
        }

        foreach (var column in _columns)
        {
            if (OutputMismatch(callee, column.Name, column.Type, value: false) is { } error)
            {
                return error;
            }
        }

        foreach (var value in _values)
        {
            if (OutputMismatch(callee, value.Name, value.Type, value: true) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary><see cref="Mismatch"/> of the plan the pipeline serves now, found once per plan.</summary>
    internal string? MismatchOf(ExecutionPlan plan)
    {
        var last = Volatile.Read(ref _checked);
        if (last is null || !last.Plan.TryGetTarget(out var checkedPlan) || !ReferenceEquals(checkedPlan, plan))
        {
            // Held weakly: a caller that is not called again must not keep a plan the pipeline no longer serves.
            last = new Checked(new WeakReference<ExecutionPlan>(plan), Mismatch(PipelineInterface.Of(plan)));
            Volatile.Write(ref _checked, last);
        }

        return last.Error;
    }

    internal string? KeyMismatch(PipelineInterface callee) => callee.KeyType == CallerKeyType
        ? null
        : $"pipeline '{callee.PipelineId}' has {Name(callee.KeyType)} keys and '{Caller}' has {Name(CallerKeyType)} keys: its rows cannot be appended";

    internal static string? ReadKeyMismatch(PipelineInterface callee, EntityKeyType read) => callee.KeyType == read
        ? null
        : $"pipeline '{callee.PipelineId}' has {Name(callee.KeyType)} keys, and its ids are read as {Name(read)}";

    internal static string? InputMismatch(PipelineInterface callee, string name, ColumnType given)
    {
        if (!callee.Inputs.TryGetType(name, out var required))
        {
            return $"pipeline '{callee.PipelineId}' declares no input '{name}'";
        }

        if (RequestSchema.ConversionFailure(given, required) is { } failure)
        {
            return $"input '{name}' is passed as {given} and pipeline '{callee.PipelineId}' declares it {required}: {failure}";
        }

        // A value and its absence are judged apart, as for a fallback pipeline's inputs: the type says which values convert,
        // nullability whether none may be passed.
        return given.Nullable && !required.Nullable
            ? $"input '{name}' is passed as {given}, which may be missing, and pipeline '{callee.PipelineId}' requires it ({required})"
            : null;
    }

    /// <summary>
    /// An input <paramref name="callee"/> requires that the call cannot give it: the operator does not pass it, and the
    /// caller's request has none of that name to pass on, or one whose values do not convert.
    /// </summary>
    internal string? MissingInput(PipelineInterface callee)
    {
        foreach (var name in callee.Inputs.Names)
        {
            if (PassesInput(name))
            {
                continue;
            }

            callee.Inputs.TryGetType(name, out var required);
            if (!CallerInputs.TryGetType(name, out var given))
            {
                if (!required.Nullable)
                {
                    return $"pipeline '{callee.PipelineId}' requires input '{name}' ({required}), which the call does not pass and '{Caller}' does not declare";
                }

                continue;
            }

            if (RequestSchema.ConversionFailure(given, required) is { } failure)
            {
                return $"input '{name}' is {given} in '{Caller}' and {required} in pipeline '{callee.PipelineId}': {failure}";
            }

            if (given.Nullable && !required.Nullable)
            {
                return $"input '{name}' is optional in '{Caller}' ({given}) and required in pipeline '{callee.PipelineId}' ({required}): a request without it would fail the call";
            }
        }

        return null;
    }

    internal static string? OutputMismatch(PipelineInterface callee, string name, ColumnType into, bool value)
    {
        var (what, found) = value ? ("value", callee.TryGetValue(name, out var type)) : ("column", callee.TryGetColumn(name, out type));
        if (!found)
        {
            return $"pipeline '{callee.PipelineId}' has no output {what} '{name}'";
        }

        if (RequestSchema.ConversionFailure(type, into) is { } failure)
        {
            return $"output {what} '{name}' of pipeline '{callee.PipelineId}' is {type} and is read as {into}: {failure}";
        }

        return type.Nullable && !into.Nullable
            ? $"output {what} '{name}' of pipeline '{callee.PipelineId}' is {type}, which may be missing, and is read as {into}"
            : null;
    }

    private static string Name(EntityKeyType type) => type == EntityKeyType.Int64 ? "int64" : "string";

    private static int IndexOf(List<PipelineOutput> list, string name)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record Checked(WeakReference<ExecutionPlan> Plan, string? Error);
}

/// <summary>What a call of a pipeline returned (<see cref="OperatorContext.CallPipelineAsync"/>).</summary>
public sealed class PipelineCallResult
{
    internal PipelineCallResult(string pipelineId, bool succeeded, string? error, ResultColumns columns, ResultValues values, CachedFrom? cached = null)
    {
        PipelineId = pipelineId;
        Succeeded = succeeded;
        Error = error;
        Columns = columns;
        Values = values;
        Cached = cached;
    }

    public string PipelineId { get; }

    /// <summary>
    /// The pipeline ran and returned a result. False when it failed, is not served, does not fit the call, or had no time
    /// left: <see cref="Error"/> says which, and <see cref="Columns"/> and <see cref="Values"/> are empty.
    /// </summary>
    public bool Succeeded { get; }

    public string? Error { get; }

    /// <summary>The returned rows in rank order with the pipeline's output columns.</summary>
    public ResultColumns Columns { get; }

    /// <summary>The pipeline's request-level output values (<c>output.values</c>).</summary>
    public ResultValues Values { get; }

    /// <summary>
    /// For a result the node's cache answered (<see cref="OperatorCompileContext.TryCallPipeline"/>): the request whose
    /// run computed it. The pipeline did not run for this call: its log taps wrote under that request, not this one. Null
    /// for a result the pipeline computed for this call.
    /// </summary>
    public CachedFrom? Cached { get; }

    internal static PipelineCallResult Failed(string pipelineId, string error) => new(pipelineId, false, error, ResultColumns.Empty, ResultValues.Empty);
}

/// <summary>One call as the engine runs it: what <see cref="OperatorContext"/> hands over.</summary>
/// <param name="Given">The inputs the operator passes; null when it passes none.</param>
/// <param name="CallerInputs">The calling run's inputs: those of the same name as an input the pipeline declares are passed on.</param>
/// <param name="Sequence">The call's place among the calls its node made in the run, from 0.</param>
/// <param name="Seed">The calling run's seed; the called run's is derived from it, the node and <paramref name="Sequence"/>.</param>
/// <param name="TimeMs">The calling run's request time, which is the called run's.</param>
/// <param name="Budget">What is left of the node's budget: the called run's deadline.</param>
internal readonly record struct PipelineCallRequest(
    PipelineCall Call, RequestInputs? Given, RequestInputs CallerInputs, int Sequence, long Seed, long TimeMs, TimeSpan Budget);

/// <summary>Runs the pipelines a run's nodes call: the engine's, since it serves them.</summary>
internal interface IPipelineCalls
{
    /// <summary>
    /// Runs the pipeline for the same request. A failure is a result; throws only when the caller cancels
    /// (<paramref name="callerCancellation"/>) or on a defect.
    /// </summary>
    ValueTask<PipelineCallResult> CallAsync(PipelineCallRequest request, CancellationToken callerCancellation);
}
