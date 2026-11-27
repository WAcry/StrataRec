using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace StrataRec.Testing;

/// <summary>
/// Gives each test process (and a benchmark's) a temporary directory of its own, and removes it when the process exits.
/// Nearly every test makes a workspace (a copy of a sample, a table's snapshot, a bundle) with
/// <see cref="Directory.CreateTempSubdirectory(string?)"/> and leaves it: a run of the suites left thousands, and a
/// machine that ran them all day filled its disk within days. Under one directory a process they go with it, whatever
/// a test forgot to remove, and tests that run at once still each have a workspace of their own. What the tests start
/// (the command line tool, a sample host) inherits the directory, and is waited for by the test that started it: a
/// process left running after its parent would find the directory gone.
/// </summary>
internal static class TestTemp
{
    /// <summary>
    /// What the directories of the processes this one can see begin with. A process id means something only among those:
    /// on Linux the processes of one PID namespace, which the name says, so that a container sharing the temporary
    /// directory with another (whose ids it cannot look up) leaves the other's directories alone.
    /// </summary>
    private static readonly string Prefix = "stratarec-tests-" + ProcessNamespace() + "-";

    [ModuleInitializer]
    internal static void Use()
    {
        var system = Path.GetTempPath();
        var own = Directory.CreateTempSubdirectory(Prefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-").FullName; // named for Sweep
        foreach (var variable in (ReadOnlySpan<string>)["TMPDIR", "TMP", "TEMP"]) // what Path.GetTempPath reads, here and on Windows
        {
            Environment.SetEnvironmentVariable(variable, own);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(own);
        Sweep(system, own);
    }

    /// <summary>
    /// A process that was killed (a run that timed out, a mutation run's) left its directory: this one removes those
    /// whose process is no longer running. Not by a directory's age: a process that works in directories under its own
    /// does not touch it, and a run of a day is not over. A process id taken again by another process keeps a directory
    /// until that one ends, which costs nothing.
    /// </summary>
    private static void Sweep(string system, string own)
    {
        try
        {
            foreach (var left in Directory.EnumerateDirectories(system, Prefix + "*"))
            {
                var name = Path.GetFileName(left.AsSpan())[Prefix.Length..];
                var dash = name.IndexOf('-');
                if (left != own && dash > 0 && int.TryParse(name[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var process) && !Runs(process))
                {
                    Remove(left);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process's directory, or one going as it is listed: its own to remove.
        }
    }

    /// <summary>The PID namespace's number on Linux (the link reads <c>pid:[4026531836]</c>); 0 where there is none to tell.</summary>
    private static string ProcessNamespace()
    {
        try
        {
            var link = File.ResolveLinkTarget("/proc/self/ns/pid", returnFinalTarget: false)?.Name ?? string.Empty;
            var digits = string.Concat(link.Where(char.IsAsciiDigit));
            return digits.Length > 0 ? digits : "0";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "0";
        }
    }

    private static bool Runs(int process)
    {
        try
        {
            using var running = Process.GetProcessById(process);
            return !running.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // there, and not this user's to ask about
        }
    }

    private static void Remove(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open, or already gone: what is left goes when a later run finds its process ended.
        }
    }
}
