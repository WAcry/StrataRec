using System.Text.Json;
using StrataRec.Core.Bindings;
using StrataRec.Models;
using StrataRec.Models.Onnx;
using StrataRec.Testing;
using Xunit.Abstractions;

namespace StrataRec.UnitTests.Models.Onnx;

/// <summary>
/// What one <c>local_onnx@1</c> call allocates on the managed heap (design 4.8): tensors wrap the caller's buffers, so
/// the cost is per call and per tensor, never per row. Measured over many calls of the sample's ctr model at two batch
/// sizes; equal costs show nothing scales with the rows. Runs alone (<see cref="AllocationMeasurement"/>), since the
/// dispatch to the thread pool is measured process-wide.
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class OnnxAllocationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>
    /// The budgets the numbers are held to. Measured (ONNX Runtime 1.30, .NET 8, two inputs and one output): 800 B for the
    /// run itself (its tensors, shapes and run options, and what ONNX Runtime's managed API adds), and 928 B for a call
    /// through <see cref="OnnxModelClient.InvokeAsync"/>, which adds the task that carries the run to the thread pool.
    /// Recorded in docs/decisions.md.
    /// </summary>
    private const long RunBudget = 1_024;
    private const long CallBudget = 1_280;
    private const int Calls = 2_000;

    private readonly string _root = Directory.CreateTempSubdirectory("stratarec-onnx-alloc-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ACallAllocatesAFixedSmallAmount_WhateverItsRows()
    {
        File.WriteAllBytes(Path.Combine(_root, "ctr.onnx"), OnnxModels.CtrModel());
        using var client = (OnnxModelClient)new OnnxModelClientFactory().Create(new BindingContext("ctr_model", Settings("""{ "uri": "ctr.onnx" }"""), _root, null));
        await client.LoadAsync(CancellationToken.None);

        var small = Measure(client, rows: 16);
        var large = Measure(client, rows: 1_024);
        var call = await MeasureCalls(client, rows: 1_024);
        output.WriteLine($"run: {small} B/call at 16 rows, {large} B/call at 1024 rows; InvokeAsync: {call} B/call");

        Assert.InRange(large, 0, RunBudget);
        Assert.InRange(large - small, -64, 64); // nothing per row
        Assert.InRange(call, 0, CallBudget);
    }

    /// <summary>The run on the current thread, per call.</summary>
    private static long Measure(OnnxModelClient client, int rows)
    {
        var (call, writer) = Call(rows);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(client.Run(call, writer, CancellationToken.None).Succeeded);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++)
        {
            client.Run(call, writer, CancellationToken.None);
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;
    }

    /// <summary>Calls through <see cref="OnnxModelClient.InvokeAsync"/>, process-wide (the run is on the pool), per call.</summary>
    private static async Task<long> MeasureCalls(OnnxModelClient client, int rows)
    {
        var (call, writer) = Call(rows);
        for (var i = 0; i < 100; i++)
        {
            Assert.True((await client.InvokeAsync(call, writer, CancellationToken.None)).Succeeded);
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Calls; i++)
        {
            await client.InvokeAsync(call, writer, CancellationToken.None);
        }

        return (GC.GetTotalAllocatedBytes(precise: true) - before) / Calls;
    }

    private static (ModelCall Call, IModelOutputWriter Writer) Call(int rows)
    {
        var ctr = new float[rows];
        var followers = new float[rows];
        for (var i = 0; i < rows; i++)
        {
            ctr[i] = i * 0.001f;
            followers[i] = i % 10;
        }

        var call = new ModelCall
        {
            Model = "ctr_model",
            Inputs = new[] { NamedInput.FromFloat32("smoothed_ctr", [rows], ctr), NamedInput.FromFloat32("log_followers", [rows], followers) },
            BatchSize = rows,
        };
        return (call, new ReusedWriter(rows));
    }

    private static Dictionary<string, JsonElement> Settings(string json)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonDocument.Parse(json).RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        return values;
    }

    /// <summary>Hands out one buffer again and again, as a caller with rented buffers does: its own cost is not measured.</summary>
    private sealed class ReusedWriter(int rows) : IModelOutputWriter
    {
        private readonly float[] _buffer = new float[rows];

        public Memory<float> GetFloat32(string name, int elementCount) => _buffer.AsMemory(0, elementCount);

        public void SetText(string name, string text)
        {
        }
    }
}
