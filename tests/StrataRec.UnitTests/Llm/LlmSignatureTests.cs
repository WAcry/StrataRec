using StrataRec.Models;
using static StrataRec.UnitTests.Llm.LlmFixture;

namespace StrataRec.UnitTests.Llm;

/// <summary>
/// <c>LlmAnnotator@1</c> and <c>GenerativeRetriever@1</c> check every version a request may call against the prompts they
/// send and the output they read, as <c>LlmReranker@1</c> does (<see cref="LlmRerankerTests"/>); v2 differs in each way.
/// </summary>
public sealed class LlmSignatureTests
{
    public static TheoryData<string, string, string, string, string> Mismatches()
    {
        var data = new TheoryData<string, string, string, string, string>();
        foreach (var op in new[] { "LlmAnnotator@1", "GenerativeRetriever@1" })
        {
            data.Add(op, "float_input", "/nodes/rank/config/prompts/user", "MODEL_TENSOR_MISMATCH",
                $"Model 'llm' version 'v2' declares input 'user' as float32 [-1], but {op} sends a rendered prompt, which is text.");
            data.Add(op, "extra_input", "/nodes/rank/config/model", "MODEL_INPUT_MISSING",
                "Model 'llm' version 'v2' requires input 'system', which 'prompts' does not supply.");
            data.Add(op, "no_input", "/nodes/rank/config/prompts/user", "MODEL_SIGNATURE", "Model 'llm' version 'v2' has no input 'user'.");
            data.Add(op, "float_output", "/nodes/rank/config/output", "MODEL_TENSOR_UNSUPPORTED", "Model 'llm' version 'v2' declares output 'answer' as float32 [-1].");
            data.Add(op, "no_output", "/nodes/rank/config/output", "MODEL_SIGNATURE", "Model 'llm' version 'v2' has no output 'answer'.");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void EveryVersionsSignature_IsCheckedAtLoad(string op, string v2, string pointer, string code, string message)
    {
        var text = new TensorSpec("user", TensorElementType.Text, Array.Empty<int>());
        var answer = new TensorSpec("answer", TensorElementType.Text, new[] { 1 }); // a text's shape is not checked
        var signature = v2 switch
        {
            "float_input" => new ModelSignature(new[] { new TensorSpec("user", TensorElementType.Float32, new[] { -1 }) }, new[] { answer }),
            "extra_input" => new ModelSignature(new[] { text, new TensorSpec("system", TensorElementType.Text, Array.Empty<int>()) }, new[] { answer }),
            "no_input" => new ModelSignature(Array.Empty<TensorSpec>(), new[] { answer }),
            "float_output" => new ModelSignature(new[] { text }, new[] { new TensorSpec("answer", TensorElementType.Float32, new[] { -1 }) }),
            _ => new ModelSignature(new[] { text }, new[] { new TensorSpec("reply", TensorElementType.Text, Array.Empty<int>()) }),
        };
        var models = new TextModels(_ => ("[]", ModelStatus.Ok), version => version == "v2" ? signature : new ModelSignature(new[] { text }, new[] { answer }));
        const string Extra = """, "version_param": "llm_version" """;
        var config = op == "LlmAnnotator@1" ? Annotate("{{col.entity_id}}", extra: Extra) : Retrieve("ids", extra: Extra);

        var error = Error(TryCompile(models, config, op, columns: NullableRecallColumns));

        Assert.Equal((pointer, code, message), (error.Pointer, error.Code, error.Message));
        Assert.False(string.IsNullOrEmpty(error.Hint));
    }

    [Theory]
    [InlineData("LlmAnnotator@1")]
    [InlineData("GenerativeRetriever@1")]
    public void MatchingVersions_Load(string op)
    {
        var models = new TextModels(_ => ("[]", ModelStatus.Ok), _ => TextModels.Text("user"));
        const string Extra = """, "version_param": "llm_version" """;
        var config = op == "LlmAnnotator@1" ? Annotate("{{col.entity_id}}", extra: Extra) : Retrieve("ids", extra: Extra);

        var compilation = TryCompile(models, config, op, columns: NullableRecallColumns);

        Assert.True(compilation.Succeeded, string.Join("\n", compilation.Diagnostics));
    }
}
