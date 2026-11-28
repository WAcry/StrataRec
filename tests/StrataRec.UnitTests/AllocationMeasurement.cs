namespace StrataRec.UnitTests;

/// <summary>
/// Classes that assert zero steady-state allocation with <see cref="GC.GetAllocatedBytesForCurrentThread"/> run alone.
/// <c>ArrayPool.Shared</c> caches one array per size per thread and shares the rest through per-core stacks; an
/// evaluation that rents two arrays of the same size (registers plus a sorted context list) can find those stacks
/// drained by other test threads and allocate.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurement
{
    public const string Name = "Allocation measurement";
}
