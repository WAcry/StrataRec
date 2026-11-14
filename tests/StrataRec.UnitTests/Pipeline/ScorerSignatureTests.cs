using StrataRec.Core.Types;
using StrataRec.Models;
using StrataRec.Pipeline.Operators.Standard;

namespace StrataRec.UnitTests.Pipeline;

/// <summary>
/// The one rule for which tensors <c>ModelScorer@1</c> sends and which model signatures it accepts (design 6.9): the tensor
/// of an input follows the type of the column it maps, and a model must declare exactly the mapped inputs, each as that
/// tensor, and every mapped output as float32 [-1]. Each mismatch names where to fix it, what is sent and what to change.
/// </summary>
public sealed class ScorerSignatureTests
{
    private static readonly int[] Rows = { -1 };

    private static readonly ScorerSignature.MappedInput[] Inputs =
    {
        new("ctr", "col.item.ctr", ColumnType.Float32),
        new("followers", "col.author.followers", ColumnType.Float32),
    };

    private static readonly Dictionary<string, string> Outputs = new(StringComparer.Ordinal) { ["p_click"] = "score.ctr" };

    [Theory]
    [InlineData("float32", "float32 [-1]")]
    [InlineData("float64", "float32 [-1]")]
    [InlineData("bool", "float32 [-1]")]
    [InlineData("int32", "int64 [-1]")]
    [InlineData("int64", "int64 [-1]")]
    [InlineData("vec<float32,8>", "float32 [-1, 8]")]
    [InlineData("float32?", null)]
    [InlineData("int64?", null)]
    [InlineData("vec<float32,8>?", null)]
    [InlineData("string", null)]
    [InlineData("text", null)]
    [InlineData("list<int32>", null)]
    [InlineData("list<int64>", null)]
    [InlineData("list<string>", null)]
    public void TheTensor_FollowsTheColumnType(string type, string? tensor)
    {
        var sent = ScorerSignature.TryGetInputTensor(ColumnType.Parse(type), out var t) ? t.ToString() : null;

        Assert.Equal(tensor, sent);
    }

    [Fact]
    public void ASignatureDeclaringWhatEachColumnGives_ForExactlyTheMappedInputs_IsAccepted()
    {
        var inputs = new ScorerSignature.MappedInput[]
        {
            new("ctr", "col.item.ctr", ColumnType.Float64),
            new("safe", "col.item.safe", ColumnType.Bool),
            new("item_id", "col.entity_id", ColumnType.Int64),
            new("category_id", "col.item.category_id", ColumnType.Int32),
            new("item_emb", "col.item.emb", ColumnType.Vector(8)),
            new("user_emb", "ctx.user.emb", ColumnType.Vector(16)),
        };
        var signature = Signature(
            new[]
            {
                Float("ctr"), Float("safe"), Spec("item_id", TensorElementType.Int64, -1), Spec("category_id", TensorElementType.Int64, -1),
                Spec("item_emb", TensorElementType.Float32, -1, 8), Spec("user_emb", TensorElementType.Float32, -1, 16),
            },
            new[] { Float("p_click"), Float("p_like") });

        Assert.Empty(ScorerSignature.Check("Model 'm' version 'default'", inputs, Outputs, signature)); // an unread output is fine
    }

    [Theory]
    [InlineData("int64", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "int64 [-1]", "a float32 derived feature of col.x")]
    [InlineData("int32", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "int64 [-1]", "a float32 derived feature of col.x")]
    [InlineData("float32", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "float32 [-1]", "an int64 derived feature of col.x")]
    [InlineData("bool", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "float32 [-1]", "an int64 derived feature of col.x")]
    [InlineData("vec<float32,8>", TensorElementType.Float32, new[] { -1, 16 }, "float32 [-1, 16]", "float32 [-1, 8]", "a vec<float32,16> derived feature of col.x")]
    [InlineData("vec<float32,8>", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "float32 [-1, 8]", "a float32 derived feature of col.x")]
    [InlineData("vec<float32,8>", TensorElementType.Int64, new[] { -1, 8 }, "int64 [-1, 8]", "float32 [-1, 8]", null)]
    [InlineData("float64", TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]", "float32 [-1]", "a vec<float32,2> derived feature of col.x")]
    public void AnInputDeclaredOtherwiseThanItsColumnGives_IsAMismatch_SayingWhatIsSentAndHowToFixIt(
        string column, TensorElementType declaredType, int[] declaredShape, string declared, string sent, string? mapInstead)
    {
        var inputs = new ScorerSignature.MappedInput[] { new("x", "col.x", ColumnType.Parse(column)) };

        var problems = ScorerSignature.Check("Model 'm' version 'v2'", inputs, Outputs,
            Signature(new[] { new TensorSpec("x", declaredType, declaredShape) }, new[] { Float("p_click") }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/inputs/x", "MODEL_TENSOR_MISMATCH"), (problem.ConfigKey, problem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares input 'x' as {declared}, but it maps col.x ({column}), which ModelScorer@1 sends as {sent}.", problem.Message);
        Assert.StartsWith($"declare input 'x' as {sent} in the model's signature", problem.Hint);
        if (mapInstead is null)
        {
            Assert.Contains($"no column gives {declared}", problem.Hint);
        }
        else
        {
            Assert.Contains($", or map {mapInstead} instead", problem.Hint);
        }
    }

    [Theory]
    [InlineData(TensorElementType.Float32, new[] { 256 }, "float32 [256]")]
    [InlineData(TensorElementType.Float32, new int[0], "float32 []")]
    [InlineData(TensorElementType.Float32, new[] { -1, -1 }, "float32 [-1, -1]")]
    [InlineData(TensorElementType.Float32, new[] { -1, 2, 3 }, "float32 [-1, 2, 3]")]
    [InlineData(TensorElementType.Text, new int[0], "text []")]
    public void AnInputTheScorerCannotBuildFromAnyColumn_IsAMismatch_WhoseHintSaysNoColumnGivesIt(TensorElementType type, int[] shape, string described)
    {
        var problems = ScorerSignature.Check("Model 'm' version 'v2'", Inputs, Outputs,
            Signature(new[] { new TensorSpec("ctr", type, shape), Float("followers") }, new[] { Float("p_click") }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/inputs/ctr", "MODEL_TENSOR_MISMATCH"), (problem.ConfigKey, problem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares input 'ctr' as {described}, but it maps col.item.ctr (float32), which ModelScorer@1 sends as float32 [-1].",
            problem.Message);
        Assert.Equal($"declare input 'ctr' as float32 [-1] in the model's signature; no column gives {described}, so a model that needs it is called "
            + "from an operator that builds other tensors", problem.Hint);
    }

    [Theory]
    [InlineData(TensorElementType.Int64, new[] { -1 }, "int64 [-1]")]
    [InlineData(TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]")]
    [InlineData(TensorElementType.Float32, new[] { 256 }, "float32 [256]")]
    [InlineData(TensorElementType.Text, new int[0], "text []")]
    public void AnOutputTheScorerCannotRead_IsUnsupported_AtItsMapping(TensorElementType type, int[] shape, string described)
    {
        var problems = ScorerSignature.Check("Model 'm' version 'v2'", Inputs, Outputs,
            Signature(new[] { Float("ctr"), Float("followers") }, new[] { new TensorSpec("p_click", type, shape) }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/outputs/p_click", "MODEL_TENSOR_UNSUPPORTED"), (problem.ConfigKey, problem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares output 'p_click' as {described}.", problem.Message);
        Assert.Equal(ScorerSignature.TensorHint, problem.Hint);
        Assert.Contains("reads every output as float32 [-1]", problem.Hint);
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
        var inputs = Inputs.Append(new ScorerSignature.MappedInput("user/emb", "col.user.emb", ColumnType.Vector(4))).ToArray();
        var outputs = new Dictionary<string, string>(Outputs, StringComparer.Ordinal) { ["p_like"] = "score.like" };

        var problems = ScorerSignature.Check("Model 'm' version 'default'", inputs, outputs,
            Signature(new[] { Float("followers"), Float("ctr") }, new[] { Float("p_click") }));

        Assert.Equal(new[] { ("/inputs/user~1emb", "MODEL_SIGNATURE"), ("/outputs/p_like", "MODEL_SIGNATURE") }, problems.Select(p => (p.ConfigKey, p.Code)));
        Assert.Equal("the model's inputs are: ctr, followers", problems[0].Hint);
        Assert.Equal("the model's outputs are: p_click", problems[1].Hint);
    }

    [Theory]
    [InlineData("float32?", "Model input 'x' maps col.x, which is nullable (float32?): a model needs a value in every row.", "give col.x a default")]
    [InlineData("vec<float32,4>?", "Model input 'x' maps col.x, which is nullable (vec<float32,4>?): a model needs a value in every row.", "'??'")]
    [InlineData("string", "Model input 'x' maps col.x (string), which ModelScorer@1 cannot send as a tensor.", "int32, int64 or vec<float32,N>")]
    [InlineData("list<int64>", "Model input 'x' maps col.x (list<int64>), which ModelScorer@1 cannot send as a tensor.", "an operator that builds other tensors")]
    public void AColumnTypeTheScorerCannotSend_IsAnInputTypeError(string type, string message, string hint)
    {
        var problem = ScorerSignature.UnsendableInput("x", "col.x", ColumnType.Parse(type));

        Assert.Equal(("/inputs/x", "MODEL_INPUT_TYPE", message), (problem.ConfigKey, problem.Code, problem.Message));
        Assert.Contains(hint, problem.Hint);
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

    private static TensorSpec Spec(string name, TensorElementType type, params int[] shape) => new(name, type, shape);

    private static ModelSignature Signature(TensorSpec[] inputs, TensorSpec[] outputs) => new(inputs, outputs);
}
