using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using StrataRec.Artifacts.Model;

namespace StrataRec.Pipeline.Replay;

/// <summary>
/// An exception a recorded call threw whose type the replay could not create again (the replaying process has not loaded
/// the type, the identity is ambiguous, or the type has no <c>(string message)</c> constructor): thrown with the original
/// message and type name.
/// </summary>
public sealed class ReplayedCallException(string message, string? originalType = null) : Exception(message)
{
    /// <summary>
    /// Readable full name of the exception type the client threw in the recording, with the arguments of a generic type in
    /// brackets as <see cref="Type.ToString"/> prints them (e.g. <c>Backend.RetryException`1[System.Int32]</c>); null when
    /// the bundle did not record it.
    /// </summary>
    public string? OriginalType { get; } = originalType;
}

/// <summary>
/// The exceptions a replay throws for recorded calls that threw (the contract of
/// <see cref="Operators.OperatorContext.InvokeModelAsync"/>), and the type identities a recording stores for them.
/// </summary>
/// <remarks>
/// <para>A type is found again only among assemblies the process has already loaded: in the loaded assemblies whose simple
/// name is the recorded one, among the types they define. Nothing here calls <see cref="Type.GetType(string)"/>,
/// <see cref="Assembly.GetType(string)"/> or <see cref="Module.GetType(string)"/>, which follow type forwarders and load
/// assemblies: a bundle must not make the replaying process load code. A name that matches no type, or more than one (the
/// same assembly name loaded twice), falls back to <see cref="ReplayedCallException"/> rather than picking one.</para>
/// <para>Resolutions are cached per identity, and a cached resolution is used only while no assembly has loaded since it
/// was made: a type missing before may exist after.</para>
/// </remarks>
internal static class ReplayedExceptions
{
    private static readonly ConcurrentDictionary<string, (int Generation, Type? Type)> Resolved = new(StringComparer.Ordinal);
    private static int _generation;

    static ReplayedExceptions() => AppDomain.CurrentDomain.AssemblyLoad += static (_, _) => Interlocked.Increment(ref _generation);

    /// <summary>The identity a recording stores for a type: the type (or its generic definition), its assembly, its arguments.</summary>
    public static ReplayTypeIdentity Identity(Type type)
    {
        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        List<ReplayTypeIdentity>? args = null;
        if (type.IsConstructedGenericType)
        {
            var arguments = type.GetGenericArguments();
            args = new List<ReplayTypeIdentity>(arguments.Length);
            foreach (var argument in arguments)
            {
                args.Add(Identity(argument));
            }
        }

        return new ReplayTypeIdentity { Name = definition.FullName ?? definition.Name, Assembly = definition.Assembly.GetName().Name ?? string.Empty, Args = args };
    }

    /// <summary>The identity's full name with generic arguments in brackets, as <see cref="Type.ToString"/> prints a closed generic type.</summary>
    public static string Readable(ReplayTypeIdentity identity)
    {
        if (identity.Args is not { Count: > 0 } args)
        {
            return identity.Name;
        }

        var text = new StringBuilder(identity.Name).Append('[');
        for (var i = 0; i < args.Count; i++)
        {
            text.Append(i == 0 ? string.Empty : ",").Append(Readable(args[i]));
        }

        return text.Append(']').ToString();
    }

    /// <summary>
    /// The exception to throw for a call recorded as throwing <paramref name="type"/>: a <see cref="TimeoutException"/>
    /// stays a <see cref="TimeoutException"/> and cancellation an <see cref="OperationCanceledException"/>; any other type
    /// is created again through its public <c>(string message)</c> constructor when it is already loaded and derives from
    /// <see cref="Exception"/>; otherwise a <see cref="ReplayedCallException"/> carries the type name.
    /// </summary>
    /// <param name="timedOut">The recorded outcome was a timeout: without a type, it is replayed as cancellation.</param>
    public static Exception Create(ReplayTypeIdentity? type, string? message, bool timedOut, string fallbackMessage)
    {
        var text = message ?? fallbackMessage;
        if (type is null)
        {
            return timedOut ? new OperationCanceledException(text) : new ReplayedCallException(text);
        }

        // The core library's own types need no search: it is loaded once and its name is unique. A namesake defined in
        // another assembly goes through the identity lookup like any other type.
        if (type.Args is not { Count: > 0 } && type.Assembly == CoreLibrary)
        {
            switch (type.Name)
            {
                case "System.TimeoutException":
                    return new TimeoutException(text);
                case "System.OperationCanceledException":
                    return new OperationCanceledException(text);
            }
        }

        if (Resolve(type) is { } resolved && typeof(Exception).IsAssignableFrom(resolved) && !resolved.IsAbstract && !resolved.ContainsGenericParameters
            && resolved.GetConstructor(new[] { typeof(string) }) is { } constructor)
        {
            try
            {
                return (Exception)constructor.Invoke(new object[] { text });
            }
            catch (TargetInvocationException)
            {
                // The constructor itself threw: fall back to the type name.
            }
        }

        return new ReplayedCallException(text, Readable(type));
    }

    private static readonly string CoreLibrary = typeof(object).Assembly.GetName().Name!;

    private static Type? Resolve(ReplayTypeIdentity identity)
    {
        var key = Key(identity);
        var generation = Volatile.Read(ref _generation); // read before the search: a load during it makes the entry stale
        if (Resolved.TryGetValue(key, out var cached) && cached.Generation == generation)
        {
            return cached.Type;
        }

        var type = Find(identity);
        Resolved[key] = (generation, type);
        return type;
    }

    /// <summary>The one loaded type with this identity; null when there is none or more than one.</summary>
    private static Type? Find(ReplayTypeIdentity identity)
    {
        Type? match = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != identity.Assembly)
            {
                continue;
            }

            foreach (var type in DefinedTypes(assembly))
            {
                if (type is not null && type.FullName == identity.Name)
                {
                    if (match is not null)
                    {
                        return null; // the same identity twice (an assembly name loaded twice): never guess
                    }

                    match = type;
                }
            }
        }

        if (match is null)
        {
            return null;
        }

        if (identity.Args is not { Count: > 0 } args)
        {
            return match.IsGenericTypeDefinition ? null : match;
        }

        if (!match.IsGenericTypeDefinition || match.GetGenericArguments().Length != args.Count)
        {
            return null;
        }

        var arguments = new Type[args.Count];
        for (var i = 0; i < arguments.Length; i++)
        {
            if (Resolve(args[i]) is not { } argument)
            {
                return null;
            }

            arguments[i] = argument;
        }

        try
        {
            return match.MakeGenericType(arguments);
        }
        catch (ArgumentException)
        {
            return null; // the arguments violate the definition's constraints
        }
    }

    /// <summary>The types an assembly defines; when some cannot load, the ones that did.</summary>
    private static Type?[] DefinedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types;
        }
    }

    /// <summary>A cache key that names every part of the identity, length-prefixed so no name can imitate another.</summary>
    private static string Key(ReplayTypeIdentity identity)
    {
        var key = new StringBuilder();
        Append(key, identity);
        return key.ToString();

        static void Append(StringBuilder key, ReplayTypeIdentity identity)
        {
            key.Append(identity.Name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(identity.Name)
                .Append(identity.Assembly.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(identity.Assembly)
                .Append('[');
            foreach (var argument in identity.Args ?? new List<ReplayTypeIdentity>())
            {
                Append(key, argument);
            }

            key.Append(']');
        }
    }
}
