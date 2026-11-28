using StrataRec.Pipeline.Operators;

namespace StrataRec.IntegrationTests;

public sealed class OperatorEffectsTests
{
    [Fact]
    public void AnOperatorWritingAColumn_WithoutColumnWriter_IsAnAuthorError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MiniWorkspace.TryCompile(
            """ "r": { "client": "memory_list@1", "items": [1] } """,
            """
            { "pipeline_id": "p",
              "columns": { "score.s": { "type": "float64", "default": 0.0 } },
              "param_schema": { "q": { "type": "map<string,int64>" }, "k": { "type": "int64" } },
              "params": { "q": { "r": 10 }, "k": 10 },
              "nodes": {
                "recall": { "order": 100, "op": "MultiSourceRecall@1", "config": { "quotas_param": "q" } },
                "sorter": { "order": 200, "op": "Sorter@1", "config": {} } },
              "output": { "limit_param": "k", "columns": {} } }
            """,
            operators: OperatorRegistry.CreateDefault().Add(new SorterFactory())));

        Assert.Equal("Operator 'Sorter@1' writes column 'score.s' but its descriptor declares neither ColumnWriter nor RowSource.", error.Message);
    }

    public sealed class SorterConfig
    {
    }

    private sealed class SorterFactory : IOperatorFactory
    {
        public OperatorDescriptor Descriptor { get; } = new("Sorter", 1, Effects.RowOrder, typeof(SorterConfig), "Orders rows, and writes a score.");

        public Operator? Compile(OperatorCompileContext context) =>
            context.TryWriteColumn("score.s", "/output_col", null, out _) ? null : null;
    }
}
