namespace StrataRec.UnitTests;

/// <summary>
/// Classes that assert zero steady-state allocation with <see cref="GC.GetAllocatedBytesForCurrentThread"/> run
/// alone. <c>ArrayPool.Shared</c> caches one array per size per thread and shares the rest through per-core
/// stacks; an evaluation that rents two arrays of the same size (registers plus a sorted context list) can find
/// those stacks drained by other test threads and allocate. That is the shared pool under concurrency, not an
/// allocation in our code, and it made these tests fail intermittently in parallel runs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurement
{
    public const string Name = "Allocation measurement";
}
