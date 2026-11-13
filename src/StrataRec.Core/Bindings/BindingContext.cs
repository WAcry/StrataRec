using System.Text.Json;

namespace StrataRec.Core.Bindings;

/// <summary>
/// What a factory gets to create the instance one binding names (design 6.4): a model client, a candidate source or a
/// feature connector. The engine shares that instance with every plan whose binding is the same, so a factory only reads
/// its settings here and does no I/O: what needs I/O (a model file, a connection, a secret) happens in
/// <see cref="ILoadable.LoadAsync"/>, when an engine loads a plan that uses the instance, never while a plan compiles
/// (<c>stratarec validate</c> creates instances without loading them, so it needs neither the files nor the network).
/// </summary>
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

    /// <summary>The binding's own settings (everything but what the framework interprets, such as <c>client</c> and <c>timeout_ms</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement> Settings { get; }

    /// <summary>
    /// The absolute directory relative paths in <see cref="Settings"/> resolve against (the configuration root), pinned when
    /// the plan was compiled: part of the instance's identity, so two plans whose relative paths name different files
    /// never share an instance.
    /// </summary>
    public string BaseDirectory { get; }

    /// <summary>The host's secret store, used by <see cref="SecretReference.ResolveAsync"/>; null when the host has none.</summary>
    internal ISecretResolver? Secrets { get; }

    /// <summary>
    /// The absolute directory relative paths resolve against: <paramref name="baseDirectory"/> made absolute against the
    /// current directory as it is now, or the current directory when it is null or empty. Pinned once, so a later change of
    /// the process's current directory changes neither what an instance reads nor which instances plans share.
    /// </summary>
    public static string PinBaseDirectory(string? baseDirectory) =>
        string.IsNullOrEmpty(baseDirectory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(baseDirectory);

    /// <summary>
    /// The setting <paramref name="setting"/> as a reference to a secret (<c>"secret://interaction-store-token"</c>,
    /// design 6.4 item 4), to resolve at load; null when the binding does not have the setting.
    /// </summary>
    /// <exception cref="BindingException">The setting is not a <c>secret://</c> reference; the pointer is the setting's.</exception>
    public SecretReference? SecretReference(string setting)
    {
        if (!Settings.TryGetValue(setting, out var value))
        {
            return null;
        }

        var pointer = "/" + EscapePointerToken(setting);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is null || !text.StartsWith(Bindings.SecretReference.Scheme, StringComparison.Ordinal) || text.Length == Bindings.SecretReference.Scheme.Length)
        {
            // The value itself may be a credential pasted in by mistake: never quote it.
            throw new BindingException(pointer, $"'{setting}' must be a reference to a secret, not a value.",
                $"write \"{setting}\": \"{Bindings.SecretReference.Scheme}<name>\" and add the secret <name> to the host's secret store");
        }

        return new SecretReference(text[Bindings.SecretReference.Scheme.Length..], pointer, Secrets);
    }

    /// <summary>
    /// The setting <paramref name="setting"/> as a local file: a path relative to <see cref="BaseDirectory"/>, an absolute
    /// path, or a <c>file://</c> URI. The syntax is checked here, so a malformed value is a configuration error that
    /// <c>validate</c> finds; whether the file exists is checked when the instance loads.
    /// </summary>
    /// <exception cref="BindingException">The setting is missing, not a string, not local, or malformed.</exception>
    public string LocalFile(string setting, string what)
    {
        var pointer = "/" + EscapePointerToken(setting);
        if (!Settings.TryGetValue(setting, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            throw new BindingException(pointer, $"'{setting}' is required: the {what}.",
                $"give the {what} as a path relative to the configuration directory, an absolute path, or a file:// URI");
        }

        return LocalFile(value.GetString()!, pointer, what);
    }

    /// <summary><see cref="LocalFile(string, string)"/> for a value that is not a top-level setting, e.g. one entry of a map.</summary>
    /// <param name="pointer">The value's pointer within the binding, for the error.</param>
    public string LocalFile(string uri, string pointer, string what)
    {
        if (LocalPaths.TryResolve(uri, BaseDirectory, out var path, out var error))
        {
            return path;
        }

        throw new BindingException(pointer, error!,
            $"give the {what} as a path relative to the configuration directory, an absolute path, or a file:// URI such as file:///data/items.json");
    }

    /// <summary>A setting name or map key as an RFC 6901 reference token (<c>~</c> as <c>~0</c>, <c>/</c> as <c>~1</c>), to build
    /// the pointer of a <see cref="BindingException"/>.</summary>
    public static string EscapePointerToken(string token) => token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>
/// The one rule for where a binding's file setting points: a path relative to the configuration root, an absolute path,
/// or a <c>file://</c> URI; anything with another scheme is not local. Every binding that reads local files uses it, so
/// they accept the same spellings and reject the same mistakes.
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
            path = Path.GetFullPath(isFileUri ? new Uri(uri).LocalPath : Path.Combine(baseDirectory, uri)); // a rooted path stays as it is
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
