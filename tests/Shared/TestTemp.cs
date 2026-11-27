using System.Runtime.CompilerServices;

namespace StrataRec.Testing;

/// <summary>
/// Gives each test process a temporary directory of its own, and removes it when the process exits. Nearly every test
/// makes a workspace (a copy of a sample, a table's snapshot, a bundle) with
/// <see cref="Directory.CreateTempSubdirectory(string?)"/> and leaves it: a run of the suites left thousands, and a
/// machine that ran them all day filled its disk within days. Under one directory a process they go with it, whatever
/// a test forgot to remove, and tests that run at once still each have a workspace of their own. What the tests start
/// (the command line tool, a sample host) inherits the directory.
/// </summary>
internal static class TestTemp
{
    private const string Prefix = "stratarec-tests-";

    [ModuleInitializer]
    internal static void Use()
    {
        var system = Path.GetTempPath();
        var own = Directory.CreateTempSubdirectory(Prefix).FullName;
        foreach (var variable in (ReadOnlySpan<string>)["TMPDIR", "TMP", "TEMP"]) // what Path.GetTempPath reads, here and on Windows
        {
            Environment.SetEnvironmentVariable(variable, own);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(own);
        Sweep(system, own);
    }

    /// <summary>
    /// A process that was killed (a run that timed out, a mutation run's) left its directory: this one removes those a
    /// day old, which no run still uses.
    /// </summary>
    private static void Sweep(string system, string own)
    {
        try
        {
            foreach (var left in Directory.EnumerateDirectories(system, Prefix + "*"))
            {
                if (left != own && Directory.GetLastWriteTimeUtc(left) < DateTime.UtcNow.AddDays(-1))
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

    private static void Remove(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open, or already gone: what is left is swept a day later.
        }
    }
}
