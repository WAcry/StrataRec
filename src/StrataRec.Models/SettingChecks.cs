using StrataRec.Core.Bindings;

namespace StrataRec.Models;

/// <summary>The checks the built-in clients share beyond what their settings types say: a list of names, a latency.</summary>
internal static class SettingChecks
{
    /// <summary>
    /// The names of a list setting (<c>"inputs": ["a", "b"]</c>): each non-empty, none twice, each problem at its item's
    /// pointer. Whether the list may be empty is the client's to say.
    /// </summary>
    public static void Names(IReadOnlyList<string> names, string setting, string hint)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var pointer = "/" + setting + "/" + i;
            if (names[i].Length == 0)
            {
                throw new BindingException(pointer, $"'{setting}' item {i} must be a non-empty name.", hint);
            }

            if (!seen.Add(names[i]))
            {
                throw new BindingException(pointer, $"'{names[i]}' is listed twice in '{setting}'.", "list each name once");
            }
        }
    }

    /// <summary><c>latency_ms</c>: a simulated latency, 0 or more.</summary>
    public static void Latency(int latencyMs)
    {
        if (latencyMs < 0)
        {
            throw new BindingException("/latency_ms", $"'latency_ms' must be 0 or more, got {latencyMs}.",
                "give the simulated latency of every call in milliseconds, or remove it for none");
        }
    }
}
