using StrataRec.Core.Diagnostics;
using StrataRec.Core.Services;

namespace StrataRec.Pipeline.Operators;

/// <summary>
/// A service a node calls, bound at compile time (<see cref="OperatorCompileContext.Service"/>): its client, shared with
/// every plan of the same binding (design 6.4), the role it plays for the node, and its binding's timeout. Immutable and
/// shared by every request of the node; calls go through <see cref="OperatorContext.CallAsync"/>.
/// </summary>
public sealed class BoundService
{
    internal BoundService(string name, IServiceClient client, string role, int? timeoutMs)
    {
        Name = name;
        Client = client;
        TimeoutMs = timeoutMs;
        Target = new IoTarget(role, name);
    }

    /// <summary>The service's logical name, as the bindings name it.</summary>
    public string Name { get; }

    public IServiceClient Client { get; }

    /// <summary>The call's telemetry target: the role the node calls the service in (<c>model</c>, <c>recall</c>, one of its own) and the service's name.</summary>
    public IoTarget Target { get; }

    /// <summary>The binding's <c>timeout_ms</c>: the most one call may take, within the node's budget; null for none.</summary>
    public int? TimeoutMs { get; }
}
