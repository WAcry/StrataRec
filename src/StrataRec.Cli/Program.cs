using StrataRec.CommandLine;
using StrataRec.Features.Cache;
using StrataRec.Llm;
using StrataRec.Pipeline.Operators;
using StrataRec.Pipeline.Planning;
using StrataRec.Redis;

namespace StrataRec.Cli;

/// <summary>
/// <c>stratarec</c>: create, validate, build, explain, diff and run configurations locally (design 10.1, 13.4), with the
/// components this tool ships. The commands are <see cref="StrataRecCommandLine"/>'s; a binary with components of its
/// own runs the same commands with them.
/// </summary>
public static class Program
{
    public static Task<int> Main(string[] args) => StrataRecCommandLine.InvokeAsync(args, CreateEnvironment());

    /// <summary>
    /// What the tool provides to plans: the built-in registries, <c>redis@1</c> for distributed caches and the
    /// generative operators of StrataRec.Llm. <c>operators</c> and <c>schema</c> list exactly these.
    /// </summary>
    public static PlanEnvironment CreateEnvironment() => new()
    {
        Operators = OperatorRegistry.CreateDefault().AddLlm(),
        CacheStores = new FeatureCacheStoreRegistry().AddRedis(),
    };
}
