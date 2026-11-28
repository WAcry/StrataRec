using System.Text.Json;

namespace StrataRec.Core.Bindings;

/// <summary>
/// What a factory gets to create the instance one binding names: a service client, a feature connector, a distributed
/// cache store or an update source.
/// </summary>
/// <remarks>
/// <para><b>Update sources.</b> The update source of a <c>local</c> table is not a shared instance. Its context has no
/// secret store, and it is never loaded or disposed.</para>
/// </remarks>
/// <remarks>
/// <para><b>Create.</b> Two plans loading at once may both create one; the extra one is disposed without being loaded
/// or called. So <c>Create</c> only reads and checks the settings: no I/O, no connections, threads or anything else
/// that needs releasing.</para>
/// <para><b>Sharing.</b> One instance serves every plan of the engine whose binding has the same identity: section,
/// logical name, type, all of the binding's own <see cref="Settings"/> and the <see cref="BaseDirectory"/>, from the
/// same factory object. All requests of all those plans call the one instance concurrently: it must be
/// thread-safe.</para>
/// <para><b>Calls.</b> The engine may stop waiting for a call at its timeout and cancel its token; the instance stays
/// alive until the call has returned.</para>
/// <para><b>Dispose.</b> An instance that holds resources implements <see cref="IAsyncDisposable"/> (preferred) or
/// <see cref="IDisposable"/>. No call starts after the disposal begins. It may come without a load or after a failed
/// load, so it must release whatever was acquired so far.</para>
/// </remarks>
public sealed class BindingContext
{
    /// <param name="name">The logical name, e.g. <c>ctr_ranker</c>.</param>
    /// <param name="settings">The binding's own settings: every member the framework does not interpret.</param>
    /// <param name="baseDirectory">Absolute; see <see cref="PinBaseDirectory"/>.</param>
    /// <param name="secrets">The host's secret store; null when the host registered none.</param>
    public BindingContext(string name, IReadOnlyDictionary<string, JsonElement> settings, string baseDirectory, ISecretResolver? secrets)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(settings);
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ArgumentException("The base directory is pinned (absolute) before instances are created; see PinBaseDirectory.", nameof(baseDirectory));
        }

        Name = name;
        Settings = settings;
        BaseDirectory = baseDirectory;
        Secrets = secrets;
    }

    /// <summary>The logical name the configuration uses, e.g. <c>ctr_ranker</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// The binding's own settings as written (everything but what the framework interprets, such as <c>client</c> and
    /// <c>timeout_ms</c>): what the factory's settings type is bound from, and the instance's identity.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Settings { get; }

    /// <summary>
    /// The absolute directory relative paths in <see cref="Settings"/> resolve against (the configuration root), pinned when
    /// the plan was compiled: part of the instance's identity, so two plans whose relative paths name different files
    /// never share an instance.
    /// </summary>
    public string BaseDirectory { get; }

    internal ISecretResolver? Secrets { get; }

    /// <summary>
    /// The absolute directory relative paths resolve against: <paramref name="baseDirectory"/> made absolute against the
    /// current directory as it is now, or the current directory when it is null or empty. Pinned once, so a later change of
    /// the process's current directory changes neither what an instance reads nor which instances plans share.
    /// </summary>
    public static string PinBaseDirectory(string? baseDirectory) =>
        string.IsNullOrEmpty(baseDirectory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(baseDirectory);

    /// <summary>
    /// A setting's value as a reference to a secret (<c>"secret://interaction-store-token"</c>), to
    /// resolve at load: what a <see cref="Bindings.SecretReference"/> member of a factory's settings type is bound with.
    /// Call it for a value a factory reads by hand.
    /// </summary>
    /// <param name="pointer">The value's pointer within the binding, e.g. <c>/password</c>: errors are reported there,
    /// now and when the reference is resolved.</param>
    /// <exception cref="BindingException">The value is not a <c>secret://</c> reference with a name. The message never
    /// quotes it.</exception>
    public SecretReference SecretReference(JsonElement value, string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is null || !text.StartsWith(Bindings.SecretReference.Scheme, StringComparison.Ordinal) || text.Length == Bindings.SecretReference.Scheme.Length)
        {
            var setting = SettingName(pointer);
            throw new BindingException(pointer, $"'{setting}' must be a reference to a secret, not a value.",
                $"write \"{setting}\": \"{Bindings.SecretReference.Scheme}<name>\" and add the secret <name> to the host's secret store");
        }

        return new SecretReference(text[Bindings.SecretReference.Scheme.Length..], pointer, Secrets);
    }

    /// <summary>
    /// A setting's value as a local file or directory: a path relative to <see cref="BaseDirectory"/>, an absolute path, or
    /// a <c>file://</c> URI (<see cref="LocalPaths"/>); what a <see cref="Bindings.LocalFile"/> member of a factory's
    /// settings type is bound with. Call it for a value a factory reads by hand. The syntax is checked here, so a
    /// malformed value is a configuration error that <c>validate</c> finds; whether the file exists is for the instance
    /// to find when it reads it.
    /// </summary>
    /// <param name="pointer">The value's pointer within the binding, e.g. <c>/data_file</c>, for errors now and when the
    /// file is read.</param>
    /// <exception cref="BindingException">The value is not a non-empty string, not local, or malformed.</exception>
    public LocalFile LocalFile(JsonElement value, string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        const string Hint = "give a local path: relative to the configuration directory, absolute, or a file:// URI such as file:///data/items.json";
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } uri)
        {
            throw new BindingException(pointer, $"'{SettingName(pointer)}' must be a local path or a file:// URI, got {value.GetRawText()}.", Hint);
        }

        if (!LocalPaths.TryResolve(uri, BaseDirectory, out var path, out var error))
        {
            var local = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || !uri.Contains("://", StringComparison.Ordinal);
            throw new BindingException(pointer, error!, local
                ? Hint
                : "copy or sync it to a local directory and give that path (relative to the configuration directory, absolute, or a file:// URI)");
        }

        return new LocalFile(path, pointer);
    }

    /// <summary>A setting name or map key as an RFC 6901 reference token (<c>~</c> as <c>~0</c>, <c>/</c> as <c>~1</c>), to build
    /// the pointer of a <see cref="BindingException"/>.</summary>
    public static string EscapePointerToken(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string SettingName(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        return slash < 0
            ? "the setting"
            : pointer[(slash + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}

/// <summary>
/// A local file or directory a binding's setting names: the type of such a member of a factory's settings type. Bound
/// with <see cref="BindingContext.LocalFile(JsonElement, string)"/>, which resolves a relative path against the pinned
/// <see cref="BindingContext.BaseDirectory"/> when the settings are bound: every binding that reads local files accepts
/// the same spellings, and a later change of the process's current directory changes nothing. The instance reads the
/// file at load and reports a missing or unreadable one at <see cref="Pointer"/>.
/// </summary>
public sealed class LocalFile
{
    internal LocalFile(string path, string pointer)
    {
        Path = path;
        Pointer = pointer;
    }

    /// <summary>The absolute path.</summary>
    public string Path { get; }

    /// <summary>The setting's pointer within the binding, e.g. <c>/data_file</c>, for errors when the file is read.</summary>
    public string Pointer { get; }

    /// <summary>The absolute path.</summary>
    public override string ToString() => Path;
}

/// <summary>
/// The settings type of a binding type that takes no settings of its own: a binding of it with a member the framework
/// does not read is an error at that member.
/// </summary>
public sealed class NoSettings
{
}

/// <summary>
/// The one rule for where a binding's file setting points: a path relative to the configuration root, an absolute path,
/// or a <c>file://</c> URI; anything with another scheme is not local.
/// </summary>
public static class LocalPaths
{
    /// <summary>The absolute path <paramref name="uri"/> names, or false with what is wrong with it.</summary>
    /// <param name="baseDirectory">Where a relative path resolves; absolute.</param>
    public static bool TryResolve(string uri, string baseDirectory, out string path, out string? error)
    {
        path = string.Empty;
        var isFileUri = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        if (!isFileUri && uri.Contains("://", StringComparison.Ordinal))
        {
            error = $"'{uri}' is not a local path.";
            return false;
        }

        try
        {
            path = Path.GetFullPath(isFileUri ? new Uri(uri).LocalPath : Path.Combine(baseDirectory, uri));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{uri}' is not a valid local path or file:// URI ({ex.Message})";
            return false;
        }
    }
}
