using StrataRec.Models;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The one rule for which model signatures <c>ModelScorer@1</c> accepts (design 6.9): exactly the mapped inputs, each
/// and every mapped output a float32 tensor of shape [-1]. Each mismatch names where to fix it and what the scorer supports.
/// </summary>
public sealed class ScorerSignatureTests
{
    private static readonly int[] Rows = { -1 };

    private static readonly Dictionary<string, string> Inputs = new(StringComparer.Ordinal) { ["ctr"] = "col.item.ctr", ["followers"] = "col.author.followers" };

    private static readonly Dictionary<string, string> Outputs = new(StringComparer.Ordinal) { ["p_click"] = "score.ctr" };

    [Fact]
    public void ASignatureOfFloat32RowTensors_ForExactlyTheMappedInputs_IsAccepted()
    {
        // What linear@1 declares, and so what the samples use.
        var signature = Signature(new[] { Float("ctr"), Float("followers") }, new[] { Float("p_click"), Float("p_like") });

        Assert.Empty(ScorerSignature.Check("Model 'm' version 'default'", Inputs, Outputs, signature)); // an unread output is fine
    }

    [Theory]
    [InlineData(TensorElementType.Int64, new[] { -1 }, "int64 [-1]")]
    [InlineData(TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]")]
    [InlineData(TensorElementType.Float32, new[] { 256 }, "float32 [256]")]
    [InlineData(TensorElementType.Float32, new int[0], "float32 []")]
    [InlineData(TensorElementType.Text, new int[0], "text []")]
    public void AnInputOrOutputTheScorerCannotBuildOrRead_IsUnsupported_AtItsMapping(TensorElementType type, int[] shape, string described)
    {
        var input = ScorerSignature.Check("Model 'm' version 'v2'", Inputs, Outputs,
            Signature(new[] { new TensorSpec("ctr", type, shape), Float("followers") }, new[] { Float("p_click") }));
        var output = ScorerSignature.Check("Model 'm' version 'v2'", Inputs, Outputs,
            Signature(new[] { Float("ctr"), Float("followers") }, new[] { new TensorSpec("p_click", type, shape) }));

        var inputProblem = Assert.Single(input);
        Assert.Equal(("/inputs/ctr", "MODEL_TENSOR_UNSUPPORTED"), (inputProblem.ConfigKey, inputProblem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares input 'ctr' as {described}.", inputProblem.Message);
        Assert.Contains("float32 with shape [-1]", inputProblem.Hint);
        var outputProblem = Assert.Single(output);
        Assert.Equal(("/outputs/p_click", "MODEL_TENSOR_UNSUPPORTED"), (outputProblem.ConfigKey, outputProblem.Code));
        Assert.Equal(ScorerSignature.TensorHint, outputProblem.Hint);
    }

    [Fact]
    public void ASignatureInputTheConfigDoesNotSupply_IsMissing_AtTheModel()
    {
        var problems = ScorerSignature.Check("Model 'm' version 'default'", Inputs, Outputs,
            Signature(new[] { Float("ctr"), Float("followers"), Float("tenure") }, new[] { Float("p_click") }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/model", "MODEL_INPUT_MISSING"), (problem.ConfigKey, problem.Code));
        Assert.Contains("'tenure'", problem.Message);
        Assert.Contains("map 'tenure' in 'inputs'", problem.Hint);
    }

    [Fact]
    public void AMappedNameTheModelLacks_IsReportedAtItsMapping_WithTheNamesItHas()
    {
        var inputs = new Dictionary<string, string>(Inputs, StringComparer.Ordinal) { ["user/emb"] = "col.user.emb" };
        var outputs = new Dictionary<string, string>(Outputs, StringComparer.Ordinal) { ["p_like"] = "score.like" };

        var problems = ScorerSignature.Check("Model 'm' version 'default'", inputs, outputs,
            Signature(new[] { Float("followers"), Float("ctr") }, new[] { Float("p_click") }));

        Assert.Equal(new[] { ("/inputs/user~1emb", "MODEL_SIGNATURE"), ("/outputs/p_like", "MODEL_SIGNATURE") }, problems.Select(p => (p.ConfigKey, p.Code)));
        Assert.Equal("the model's inputs are: ctr, followers", problems[0].Hint);
        Assert.Equal("the model's outputs are: p_click", problems[1].Hint);
    }

    [Fact]
    public void TheRowTensorTest_AcceptsOnlyFloat32WithOneDynamicDimension()
    {
        Assert.True(ScorerSignature.IsRowTensor(Float("x")));
        Assert.False(ScorerSignature.IsRowTensor(new TensorSpec("x", TensorElementType.Int64, Rows)));
        Assert.False(ScorerSignature.IsRowTensor(new TensorSpec("x", TensorElementType.Float32, new[] { -1, -1 })));
        Assert.False(ScorerSignature.IsRowTensor(new TensorSpec("x", TensorElementType.Float32, new[] { 1 })));
    }

    private static TensorSpec Float(string name) => new(name, TensorElementType.Float32, Rows);

    private static ModelSignature Signature(TensorSpec[] inputs, TensorSpec[] outputs) => new(inputs, outputs);
}
