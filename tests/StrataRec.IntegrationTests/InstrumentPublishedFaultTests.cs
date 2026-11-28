using System.Diagnostics;
using System.Diagnostics.Metrics;
using StrataRec.Core.Diagnostics;
using StrataRec.Hosting;
using StrataRec.Logging;

namespace StrataRec.IntegrationTests;

/// <summary>
/// The first read happens once per process, so the test runs in a child process (<see cref="TimerFaultChild.Start"/>'s
/// way).
/// </summary>
public sealed class InstrumentPublishedFaultTests
{
    [Fact]
    public void AListenerThatThrowsWhenAnInstrumentIsPublished_StopsNeitherTheLogsNorTheEngine()
    {
        var output = InstrumentPublishedFaultChild.Start(out var exitCode);

        Assert.True(exitCode == 0, $"the child exited with {exitCode}:\n{output}");
        Assert.Equal("True", Value(output, "listener_threw"));
        Assert.Equal("Queued,Queued", Value(output, "admissions"));
        Assert.Equal("True", Value(output, "flushed"));
        Assert.Equal("2", Value(output, "written"));
        Assert.Equal("0", Value(output, "queued_bytes"));
        Assert.Equal("True/True", Value(output, "served"));
        Assert.Equal("True", Value(output, "flushed_after_request"));
        Assert.Equal("True", Value(output, "request_logged"));
    }

    private static string Value(string output, string key)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..].TrimEnd('\r');
            }
        }

        throw new Xunit.Sdk.XunitException($"the child printed no {key}:\n{output}");
    }
}

internal static class InstrumentPublishedFaultChild
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static string Start(out int exitCode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add(typeof(InstrumentPublishedFaultChild).Assembly.Location);
        start.ArgumentList.Add("instrument-published-fault");
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }

        exitCode = child.ExitCode;
        return stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
    }

    public static int Run()
    {
        var threw = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == StrataRecTelemetry.Name)
                {
                    Interlocked.Increment(ref threw);
                    throw new InvalidOperationException("the host's listener is broken");
                }
            },
        };
        listener.Start();

        var sink = new InMemoryLogSink();
        var logs = new LogPipeline(sink);
        var admissions = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            try
            {
                admissions.Add(logs.Submit(Batch("served")).ToString());
            }
            catch (Exception ex)
            {
                admissions.Add(ex.GetType().Name);
            }
        }

        Console.WriteLine($"admissions={string.Join(",", admissions)}");
        Console.WriteLine($"flushed={logs.FlushAsync().Wait(Patience)}");
        Console.WriteLine($"written={sink.Snapshot().Count}");
        Console.WriteLine($"queued_bytes={logs.DurableQueuedBytes}");
        Console.WriteLine($"listener_threw={Volatile.Read(ref threw) > 0}");

        string served;
        try
        {
            var host = HomeFeedTestHost.Load();
            var engine = new RecEngine(host.Environment, logs: logs);
            var load = engine.Load(host.Request);
            var result = engine.ExecuteAsync(MetricsTests.Request("child")).GetAwaiter().GetResult();
            served = $"{load.Applied}/{result.Succeeded}";
            engine.DisposeAsync().AsTask().Wait(Patience);
        }
        catch (Exception ex)
        {
            served = ex.GetType().Name;
        }

        Console.WriteLine($"served={served}");
        Console.WriteLine($"flushed_after_request={logs.FlushAsync().Wait(Patience)}");
        Console.WriteLine($"request_logged={sink.Snapshot().Count > 2}");
        logs.DisposeAsync().AsTask().Wait(Patience);
        return 0;
    }

    private static LogBatch Batch(string tap) => new()
    {
        TapId = tap,
        RequestId = "req-1",
        PipelineId = "feed",
        ManifestId = "sha256:test",
        Schema = new LogSchema("sha256:schema", tap, [], []),
        TimeMs = 1_767_225_600_000,
        Delivery = LogDelivery.Durable,
        RequestProbability = 1,
        Int64EntityIds = [],
        Ranks = [],
        RowProbability = 1,
        Columns = [],
        Context = [],
    };
}
