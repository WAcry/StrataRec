using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace StrataRec.Benchmarks;

public static class Program
{
    /// <summary>
    /// Runs the benchmarks the arguments select. The exit code is the result: BenchmarkDotNet prints a failed
    /// benchmark and returns normally, so a run that measured nothing (no benchmark selected, one that threw, a build
    /// that failed) would pass CI's smoke run.
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cache-compare")
        {
            return CacheComparison.Run(args[1..]);
        }

        var ran = 0;
        var failed = 0;
        foreach (var summary in BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args))
        {
            failed += summary.HasCriticalValidationErrors ? 1 : 0;
            foreach (var report in summary.Reports)
            {
                ran++;
                failed += Measured(report) ? 0 : 1;
            }
        }

        if (ran == 0 || failed > 0)
        {
            Console.Error.WriteLine(ran == 0 ? "No benchmark ran." : $"{failed} of {ran} benchmarks did not produce a result.");
            return 1;
        }

        return 0;
    }

    private static bool Measured(BenchmarkReport report) => report.Success && report.ResultStatistics is not null;
}
