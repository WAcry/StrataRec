namespace StrataRec.Core.Bindings;

/// <summary>
/// An instance created from a binding (model client, candidate source, feature connector) that must do I/O before it can
/// serve: read a model file, open a connection, resolve its credentials. The engine calls <see cref="LoadAsync"/> once per
/// instance, off the request path, when it loads the first plan that uses the instance, and serves that plan only after
/// it completed; plans that share the instance later find it loaded. An instance without this interface serves as
/// created.
/// </summary>
/// <remarks>
/// <para>The engine bounds the load (<c>BindingHostOptions.LoadTimeout</c>) and cancels its token when the instance is no
/// longer wanted (the plan was rejected, the engine is disposed). A failed or timed-out load rejects the plans that need
/// the instance, with an error at the binding; the instance is then never handed out again, so the next load of the
/// same binding creates a new instance and really retries. Throw <see cref="BindingException"/> to say which setting is
/// wrong and how to fix it.</para>
/// <para>The engine disposes the instance (<see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>) only after its load
/// returned, and only once no served plan uses it and no request of a replaced plan can still call it.</para>
/// </remarks>
public interface ILoadable
{
    ValueTask LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A binding's settings are wrong, or its instance failed to load: where (a JSON pointer within the binding), why, and
/// how to fix it. The engine reports it at the binding, e.g. <c>bindings:prod /models/ctr_backup/uri</c>.
/// </summary>
public sealed class BindingException : Exception
{
    /// <param name="pointer">RFC 6901 pointer within the binding, e.g. <c>/uri</c>; empty for the binding as a whole.</param>
    /// <param name="reason">What is wrong. Never put a secret's value in it.</param>
    /// <param name="hint">How to fix it.</param>
    public BindingException(string pointer, string reason, string? hint = null, Exception? innerException = null)
        : base(reason, innerException)
    {
        Pointer = pointer;
        Reason = reason;
        Hint = hint;
    }

    public string Pointer { get; }

    public string Reason { get; }

    public string? Hint { get; }
}

/// <summary>
/// The host's secret store (design 6.4 item 4): bindings name credentials only by reference (<c>secret://name</c>), and
/// instances resolve them through this when they load. Register one in the host's service container, or set it on the
/// plan environment.
/// </summary>
/// <remarks>Called at load, never on the request path, and once per load of an instance: the engine keeps no copy of a
/// value and does not cache or rotate secrets. An instance that needs a rotated credential resolves it again itself.</remarks>
public interface ISecretResolver
{
    /// <summary>The value of the secret <paramref name="name"/>, or null when the store has no secret of that name.</summary>
    ValueTask<string?> ResolveAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// A credential named in a binding (<c>"credentials": "secret://interaction-store-token"</c>), resolved by the host's
/// <see cref="ISecretResolver"/> when the instance loads. Read it at create time with
/// <see cref="BindingContext.SecretReference"/>; only the name and the setting's pointer are kept.
/// </summary>
public sealed class SecretReference
{
    /// <summary>The scheme of a reference.</summary>
    public const string Scheme = "secret://";

    private readonly ISecretResolver? _resolver;

    internal SecretReference(string name, string pointer, ISecretResolver? resolver)
    {
        Name = name;
        Pointer = pointer;
        _resolver = resolver;
    }

    /// <summary>The secret's name in the host's store.</summary>
    public string Name { get; }

    /// <summary>The pointer of the setting that holds the reference, e.g. <c>/credentials</c>, for errors.</summary>
    public string Pointer { get; }

    /// <summary>Resolves the secret; call it from <see cref="ILoadable.LoadAsync"/>.</summary>
    /// <exception cref="BindingException">The host has no secret store, or the store has no secret of this name; at <see cref="Pointer"/>.</exception>
    public async ValueTask<Secret> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_resolver is null)
        {
            throw new BindingException(Pointer, $"The binding refers to secret '{Name}', but this host has no secret store.",
                "register an ISecretResolver in the host's service container, or pass one to the engine (RecEngine secrets, BindingHostOptions.Secrets)");
        }

        var value = await _resolver.ResolveAsync(Name, cancellationToken).ConfigureAwait(false);
        return value is null
            ? throw new BindingException(Pointer, $"The host's secret store has no secret '{Name}'.", $"add the secret '{Name}' to the store, or fix the name after {Scheme}")
            : new Secret(value);
    }

    /// <summary>The reference as written (<c>secret://name</c>), never a value.</summary>
    public override string ToString() => Scheme + Name;
}

/// <summary>
/// A resolved credential. <see cref="ToString"/> never shows the value, so formatting one into a message, a log or an
/// exception does not leak it; <see cref="Reveal"/> is the one way to read it, at the point where the instance hands it to
/// its protocol (a password, a header). The engine never holds one: values flow from the store straight to the instance.
/// </summary>
[System.Diagnostics.DebuggerDisplay("[secret]")]
public sealed class Secret
{
    private readonly string _value;

    public Secret(string value) => _value = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>The value. Do not log it or put it in an error message.</summary>
    public string Reveal() => _value;

    public override string ToString() => "[secret]";
}
