using StrataRec.Core.Types;
using StrataRec.Pipeline.Operators.Standard;
using StrataRec.Core.Services;

namespace StrataRec.UnitTests.Pipeline;

public sealed class ModelTensorRuleTests
{
    private static readonly int[] Rows = { -1 };

    private static readonly ModelTensorRule.MappedInput[] Inputs =
    {
        new("ctr", "col.item.ctr", ColumnType.Float32),
        new("followers", "col.author.followers", ColumnType.Float32),
    };

    private static readonly ModelTensorRule.MappedOutput[] Outputs = { new("p_click", "/outputs/p_click") };

    [Theory]
    [InlineData("float32", "float32 [-1]")]
    [InlineData("float64", "float64 [-1]")]
    [InlineData("bool", "bool [-1]")]
    [InlineData("int32", "int32 [-1]")]
    [InlineData("int64", "int64 [-1]")]
    [InlineData("string", "string [-1]")]
    [InlineData("list<float32,8>", "float32 [-1, 8]")]
    [InlineData("list<float64,8>", "float64 [-1, 8]")]
    [InlineData("list<int32,2>", "int32 [-1, 2]")]
    [InlineData("list<int64,1>", "int64 [-1, 1]")]
    [InlineData("list<bool,3>", "bool [-1, 3]")]
    [InlineData("list<string,2>", "string [-1, 2]")]
    [InlineData("float32?", null)]
    [InlineData("int64?", null)]
    [InlineData("list<float32,8>?", null)]
    [InlineData("list<float32>", null)]
    [InlineData("list<int32>", null)]
    [InlineData("list<int64>", null)]
    [InlineData("list<string>", null)]
    public void TheTensor_FollowsTheColumnType(string type, string? tensor)
    {
        var sent = ModelTensorRule.TryGetInputTensor(ColumnType.Parse(type), out var t) ? t.ToString() : null;

        Assert.Equal(tensor, sent);
    }

    [Theory]
    [InlineData("Rows", false, 0, 5, new[] { 5 })]
    [InlineData("Rows", false, 8, 5, new[] { 5, 8 })]
    [InlineData("Rows", true, 8, 5, new[] { 5, 8 })]
    [InlineData("Pairs", false, 0, 5, new[] { 5, 2 })]
    [InlineData("Pairs", false, 8, 5, new[] { 5, 2, 8 })]
    [InlineData("Pairs", true, 0, 5, new[] { 5 })]
    [InlineData("Pairs", true, 8, 5, new[] { 5, 8 })]
    [InlineData("List", false, 0, 5, new[] { 1, 5 })]
    [InlineData("List", false, 8, 5, new[] { 1, 5, 8 })]
    [InlineData("List", true, 0, 5, new[] { 1 })]
    [InlineData("List", true, 8, 5, new[] { 1, 8 })]
    [InlineData("Pairs", false, 0, -1, new[] { -1, 2 })]
    [InlineData("List", false, 8, -1, new[] { 1, -1, 8 })]
    public void TheLayout_GivesTheLeadingDimensions_OfAColumnAndOfABroadcastValue(string layoutName, bool broadcast, int dimension, int length, int[] shape)
    {
        var layout = Enum.Parse<TensorLayout>(layoutName);
        Assert.Equal(shape, ModelTensorRule.InputShape(layout, broadcast, length, dimension));
    }

    [Fact]
    public void ASignatureDeclaringWhatEachColumnGives_ForExactlyTheMappedInputs_IsAccepted()
    {
        var inputs = new ModelTensorRule.MappedInput[]
        {
            new("ctr", "col.item.ctr", ColumnType.Float64),
            new("safe", "col.item.safe", ColumnType.Bool),
            new("item_id", "col.entity_id", ColumnType.Int64),
            new("category_id", "col.item.category_id", ColumnType.Int32),
            new("item_emb", "col.item.emb", ColumnType.List(StorageKind.Float32, fixedLength: 8)),
            new("user_emb", "ctx.user.emb", ColumnType.List(StorageKind.Float32, fixedLength: 16), Broadcast: true),
            new("flags", "col.item.flags", ColumnType.List(StorageKind.Bool, fixedLength: 2)),
            new("category", "col.item.category", ColumnType.String),
        };
        var signature = Signature(
            new[]
            {
                Spec("ctr", TensorElementType.Float64, -1), Spec("safe", TensorElementType.Bool, -1), Spec("item_id", TensorElementType.Int64, -1),
                Spec("category_id", TensorElementType.Int32, -1), Spec("flags", TensorElementType.Bool, -1, 2),
                Spec("item_emb", TensorElementType.Float32, -1, 8), Spec("user_emb", TensorElementType.Float32, -1, 16),
                Spec("category", TensorElementType.String, -1),
            },
            new[] { Float("p_click"), Float("p_like") });

        Assert.Empty(ModelTensorRule.Check("Model 'm' version 'default'", TensorLayout.Rows, inputs, Outputs, signature));
    }

    [Theory]
    [InlineData(new[] { -1, 8 })]
    [InlineData(new[] { -1, -1 })]
    public void AListsN_MayBeDeclaredAsItsSizeOrAsAny(int[] declared)
    {
        var inputs = new ModelTensorRule.MappedInput[] { new("emb", "col.item.emb", ColumnType.List(StorageKind.Float32, fixedLength: 8)) };

        Assert.Empty(ModelTensorRule.Check("Model 'm' version 'v2'", TensorLayout.Rows, inputs, Outputs,
            Signature(new[] { new TensorSpec("emb", TensorElementType.Float32, declared) }, new[] { Float("p_click") })));
    }

    [Theory]
    [InlineData("int64", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "int64 [-1]", "a float32 derived feature of col.x")]
    [InlineData("int32", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "int32 [-1]", "a float32 derived feature of col.x")]
    [InlineData("int32", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "int32 [-1]", "an int64 derived feature of col.x")]
    [InlineData("float32", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "float32 [-1]", "an int64 derived feature of col.x")]
    [InlineData("float64", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "float64 [-1]", "a float32 derived feature of col.x")]
    [InlineData("bool", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "bool [-1]", "an int64 derived feature of col.x")]
    [InlineData("string", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "string [-1]", "an int64 derived feature of col.x")]
    [InlineData("list<bool,2>", TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]", "bool [-1, 2]", "a list<float32,2> derived feature of col.x")]
    [InlineData("list<float32,8>", TensorElementType.Float32, new[] { -1, 16 }, "float32 [-1, 16]", "float32 [-1, 8]", "a list<float32,16> derived feature of col.x")]
    [InlineData("list<float32,8>", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "float32 [-1, 8]", "a float32 derived feature of col.x")]
    [InlineData("list<float32,8>", TensorElementType.Int64, new[] { -1, 8 }, "int64 [-1, 8]", "float32 [-1, 8]", "a list<int64,8> derived feature of col.x")]
    [InlineData("list<float32,8>", TensorElementType.Text, new[] { -1, 8 }, "text [-1, 8]", "float32 [-1, 8]", null)]
    [InlineData("float64", TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]", "float64 [-1]", "a list<float32,2> derived feature of col.x")]
    [InlineData("float32", TensorElementType.Float32, new[] { -1, -1 }, "float32 [-1, -1]", "float32 [-1]", "a list<float32,N> derived feature of col.x")]
    public void AnInputDeclaredOtherwiseThanItsColumnGives_IsAMismatch_SayingWhatIsSentAndHowToFixIt(
        string column, TensorElementType declaredType, int[] declaredShape, string declared, string sent, string? mapInstead)
    {
        var inputs = new ModelTensorRule.MappedInput[] { new("x", "col.x", ColumnType.Parse(column)) };

        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", TensorLayout.Rows, inputs, Outputs,
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
    [InlineData(TensorElementType.Float32, new[] { -1, 2, 3 }, "float32 [-1, 2, 3]")]
    [InlineData(TensorElementType.Text, new int[0], "text []")]
    public void AnInputTheScorerCannotBuildFromAnyColumn_IsAMismatch_WhoseHintSaysNoColumnGivesIt(TensorElementType type, int[] shape, string described)
    {
        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", TensorLayout.Rows, Inputs, Outputs,
            Signature(new[] { new TensorSpec("ctr", type, shape), Float("followers") }, new[] { Float("p_click") }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/inputs/ctr", "MODEL_TENSOR_MISMATCH"), (problem.ConfigKey, problem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares input 'ctr' as {described}, but it maps col.item.ctr (float32), which ModelScorer@1 sends as float32 [-1].",
            problem.Message);
        Assert.Equal($"declare input 'ctr' as float32 [-1] in the model's signature; no column gives {described}, so a model that needs it is called "
            + "from an operator that builds other tensors", problem.Hint);
    }

    [Theory]
    [InlineData("float32", TensorElementType.Int64, new[] { -1 }, "int64 [-1]", "float32 [-1]", ", or write it to an int64 column, which takes what the model returns")]
    [InlineData("float32", TensorElementType.Float64, new[] { -1 }, "float64 [-1]", "float32 [-1]", ", or write it to a float64 column, which takes what the model returns")]
    [InlineData("float32", TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]", "float32 [-1]", ", or write it to a list<float32,2> column, which takes what the model returns")]
    [InlineData("float32", TensorElementType.Float32, new[] { -1, -1 }, "float32 [-1, -1]", "float32 [-1]", ", or write it to a list<float32,N> column, which takes what the model returns")]
    [InlineData("float32", TensorElementType.Float32, new[] { 256 }, "float32 [256]", "float32 [-1]", "; no column takes float32 [256]")]
    [InlineData("float32", TensorElementType.Float32, new[] { -1, 2, 3 }, "float32 [-1, 2, 3]", "float32 [-1]", "; no column takes float32 [-1, 2, 3]")]
    [InlineData("float32", TensorElementType.Text, new int[0], "text []", "float32 [-1]", "; no column takes text []")]
    [InlineData("float32", TensorElementType.Float32, new[] { -1, 0 }, "float32 [-1, 0]", "float32 [-1]", "; no column takes float32 [-1, 0]")]
    [InlineData("float32", TensorElementType.Float32, new[] { -1, -2 }, "float32 [-1, -2]", "float32 [-1]", "; no column takes float32 [-1, -2]")]
    [InlineData("float32", (TensorElementType)255, new[] { -1 }, "none [-1]", "float32 [-1]", "; no column takes none [-1]")]
    [InlineData("int64?", TensorElementType.Int32, new[] { -1 }, "int32 [-1]", "int64 [-1]", ", or write it to an int32 column, which takes what the model returns")]
    [InlineData("list<float32,4>", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "float32 [-1, 4]", ", or write it to a float32 column, which takes what the model returns")]
    public void AScorerOutputDeclaredOtherwiseThanItsColumnTakes_IsAMismatch_SayingWhatIsReadAndHowToFixIt(
        string column, TensorElementType type, int[] shape, string described, string read, string otherFix)
    {
        var outputs = new ModelTensorRule.MappedOutput[] { new("p_click", "/outputs/p_click", ColumnType.Parse(column)) };

        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", TensorLayout.Rows, Inputs, outputs,
            Signature(new[] { Float("ctr"), Float("followers") }, new[] { new TensorSpec("p_click", type, shape) }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/outputs/p_click", "MODEL_TENSOR_MISMATCH"), (problem.ConfigKey, problem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares output 'p_click' as {described}, but it is written to a {column} column, which ModelScorer@1 reads as {read}.",
            problem.Message);
        Assert.Equal($"declare output 'p_click' as {read} in the model's signature{otherFix}", problem.Hint);
    }

    [Theory]
    [InlineData("float64", TensorElementType.Float64, new[] { -1 })]
    [InlineData("int64", TensorElementType.Int64, new[] { -1 })]
    [InlineData("int32?", TensorElementType.Int32, new[] { -1 })]
    [InlineData("bool", TensorElementType.Bool, new[] { -1 })]
    [InlineData("string", TensorElementType.String, new[] { -1 })]
    [InlineData("list<float32,4>", TensorElementType.Float32, new[] { -1, 4 })]
    [InlineData("list<float32,4>", TensorElementType.Float32, new[] { -1, -1 })]
    public void AScorerOutput_IsReadAsTheTypeOfItsColumn(string column, TensorElementType type, int[] shape)
    {
        var outputs = new ModelTensorRule.MappedOutput[] { new("p", "/outputs/p", ColumnType.Parse(column)) };

        Assert.Empty(ModelTensorRule.Check("Model 'm' version 'v2'", TensorLayout.Rows, Inputs, outputs,
            Signature(new[] { Float("ctr"), Float("followers") }, new[] { new TensorSpec("p", type, shape) })));
    }

    [Theory]
    [InlineData(TensorElementType.Int64, new[] { -1 }, "int64 [-1]")]
    [InlineData(TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]")]
    [InlineData(TensorElementType.Text, new int[0], "text []")]
    public void ARankerScoreThatIsNotAFloatOfTheLayoutsShape_IsUnsupported_AtItsMapping(TensorElementType type, int[] shape, string described)
    {
        var inputs = new ModelTensorRule.MappedInput[] { new("x", "col.x", ColumnType.Float32) };
        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", TensorLayout.Pairs, inputs, new ModelTensorRule.MappedOutput[] { new("p", "/output") },
            Signature(new[] { new TensorSpec("x", TensorElementType.Float32, new[] { -1, 2 }) }, new[] { new TensorSpec("p", type, shape) }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/output", "MODEL_TENSOR_UNSUPPORTED"), (problem.ConfigKey, problem.Code));
        Assert.Equal($"Model 'm' version 'v2' declares output 'p' as {described}.", problem.Message);
        Assert.Equal(ModelTensorRule.TensorHint(TensorLayout.Pairs), problem.Hint);
    }

    [Fact]
    public void ASignatureInputTheConfigDoesNotSupply_IsMissing_AtTheModel()
    {
        var problems = ModelTensorRule.Check("Model 'm' version 'default'", TensorLayout.Rows, Inputs, Outputs,
            Signature(new[] { Float("ctr"), Float("followers"), Float("tenure") }, new[] { Float("p_click") }));

        var problem = Assert.Single(problems);
        Assert.Equal(("/model", "MODEL_INPUT_MISSING"), (problem.ConfigKey, problem.Code));
        Assert.Contains("'tenure'", problem.Message);
        Assert.Contains("map 'tenure' in 'inputs'", problem.Hint);
    }

    [Fact]
    public void AMappedNameTheModelLacks_IsReportedAtItsMapping_WithTheNamesItHas()
    {
        var inputs = Inputs.Append(new ModelTensorRule.MappedInput("user/emb", "col.user.emb", ColumnType.List(StorageKind.Float32, fixedLength: 4))).ToArray();
        var outputs = Outputs.Append(new ModelTensorRule.MappedOutput("p_like", "/outputs/p_like")).ToArray();

        var problems = ModelTensorRule.Check("Model 'm' version 'default'", TensorLayout.Rows, inputs, outputs,
            Signature(new[] { Float("followers"), Float("ctr") }, new[] { Float("p_click") }));

        Assert.Equal(new[] { ("/inputs/user~1emb", "SERVICE_SIGNATURE"), ("/outputs/p_like", "SERVICE_SIGNATURE") }, problems.Select(p => (p.ConfigKey, p.Code)));
        Assert.Equal("the model's inputs are: ctr, followers", problems[0].Hint);
        Assert.Equal("the model's outputs are: p_click", problems[1].Hint);
    }

    [Theory]
    [InlineData("float32?", "Model input 'x' maps col.x, which is nullable (float32?): a model needs a value in every row.", "make col.x non-nullable (a feature with a default")]
    [InlineData("list<float32,4>?", "Model input 'x' maps col.x, which is nullable (list<float32,4>?): a model needs a value in every row.", "'??'")]
    [InlineData("list<string>", "Model input 'x' maps col.x (list<string>), a list of any length: a tensor's trailing dimension is the same in every row.",
        "e.g. list<string,64>")]
    [InlineData("list<int64>", "Model input 'x' maps col.x (list<int64>), a list of any length: a tensor's trailing dimension is the same in every row.",
        "declare the length of col.x, e.g. list<int64,64>")]
    [InlineData("list<float32>", "Model input 'x' maps col.x (list<float32>), a list of any length: a tensor's trailing dimension is the same in every row.",
        "declare the length of col.x, e.g. list<float32,64>")]
    public void AColumnTypeTheScorerCannotSend_IsAnInputTypeError(string type, string message, string hint)
    {
        var problem = ModelTensorRule.UnsendableInput("x", "col.x", ColumnType.Parse(type));

        Assert.Equal(("/inputs/x", "MODEL_INPUT_TYPE", message), (problem.ConfigKey, problem.Code, problem.Message));
        Assert.Contains(hint, problem.Hint);
    }

    [Fact]
    public void ASendableType_HasNoInputTypeError()
    {
        Assert.Throws<ArgumentException>(() => ModelTensorRule.UnsendableInput("x", "col.x", ColumnType.String));
    }

    [Fact]
    public void TheRowsScoreTest_AcceptsOnlyAFloatWithOneDynamicDimension()
    {
        Assert.True(ModelTensorRule.AcceptsScore(TensorLayout.Rows, Float("x")));
        Assert.True(ModelTensorRule.AcceptsScore(TensorLayout.Rows, new TensorSpec("x", TensorElementType.Float64, Rows)));
        Assert.False(ModelTensorRule.AcceptsScore(TensorLayout.Rows, new TensorSpec("x", TensorElementType.Int64, Rows)));
        Assert.False(ModelTensorRule.AcceptsScore(TensorLayout.Rows, new TensorSpec("x", TensorElementType.Float32, new[] { -1, -1 })));
        Assert.False(ModelTensorRule.AcceptsScore(TensorLayout.Rows, new TensorSpec("x", TensorElementType.Float32, new[] { 1 })));
    }

    [Theory]
    [InlineData("Pairs", false, "float32", TensorElementType.Float32, new[] { -1, 2 }, true)]
    [InlineData("Pairs", false, "float64", TensorElementType.Float64, new[] { -1, -1 }, true)]
    [InlineData("Pairs", false, "float64", TensorElementType.Float32, new[] { -1, 2 }, false)]
    [InlineData("Pairs", false, "int64", TensorElementType.Int64, new[] { -1, 2 }, true)]
    [InlineData("Pairs", false, "int32", TensorElementType.Float32, new[] { -1, 2 }, false)]
    [InlineData("Pairs", false, "float32", TensorElementType.Float32, new[] { 256, 2 }, false)]
    [InlineData("Pairs", false, "float32", TensorElementType.Float32, new[] { -1, 3 }, false)]
    [InlineData("Pairs", false, "float32", TensorElementType.Float32, new[] { -1 }, false)]
    [InlineData("Pairs", false, "list<float32,4>", TensorElementType.Float32, new[] { -1, 2, 4 }, true)]
    [InlineData("Pairs", false, "list<float32,4>", TensorElementType.Float32, new[] { -1, -1, -1 }, true)]
    [InlineData("Pairs", false, "list<float32,4>", TensorElementType.Float32, new[] { -1, 2, 5 }, false)]
    [InlineData("Pairs", false, "list<float32,4>", TensorElementType.Float32, new[] { -1, 4 }, false)]
    [InlineData("Pairs", true, "float32", TensorElementType.Float32, new[] { -1 }, true)]
    [InlineData("Pairs", true, "int64", TensorElementType.Int64, new[] { -1 }, true)]
    [InlineData("Pairs", true, "float32", TensorElementType.Float32, new[] { -1, 2 }, false)]
    [InlineData("Pairs", true, "float32", TensorElementType.Float32, new[] { 1 }, false)]
    [InlineData("Pairs", true, "list<float32,4>", TensorElementType.Float32, new[] { -1, 4 }, true)]
    [InlineData("Pairs", true, "list<float32,4>", TensorElementType.Float32, new[] { -1, -1 }, true)]
    [InlineData("Pairs", true, "list<float32,4>", TensorElementType.Float32, new[] { -1, 2, 4 }, false)]
    [InlineData("List", false, "float32", TensorElementType.Float32, new[] { 1, -1 }, true)]
    [InlineData("List", false, "bool", TensorElementType.Bool, new[] { -1, -1 }, true)]
    [InlineData("List", false, "bool", TensorElementType.Int64, new[] { 1, -1 }, false)]
    [InlineData("List", false, "bool", TensorElementType.Float32, new[] { 1, -1 }, false)]
    [InlineData("List", false, "list<bool,3>", TensorElementType.Bool, new[] { 1, -1, 3 }, true)]
    [InlineData("List", false, "int32", TensorElementType.Int32, new[] { 1, -1 }, true)]
    [InlineData("List", false, "int32", TensorElementType.Int64, new[] { 1, -1 }, false)]
    [InlineData("List", false, "float32", TensorElementType.Float32, new[] { 1, 50 }, false)]
    [InlineData("List", false, "float32", TensorElementType.Float32, new[] { 2, -1 }, false)]
    [InlineData("List", false, "float32", TensorElementType.Float32, new[] { -1 }, false)]
    [InlineData("List", false, "list<float32,4>", TensorElementType.Float32, new[] { 1, -1, 4 }, true)]
    [InlineData("List", false, "list<float32,4>", TensorElementType.Float32, new[] { -1, -1, -1 }, true)]
    [InlineData("List", false, "list<float32,4>", TensorElementType.Float32, new[] { 1, 4 }, false)]
    [InlineData("List", true, "float32", TensorElementType.Float32, new[] { 1 }, true)]
    [InlineData("List", true, "int64", TensorElementType.Int64, new[] { -1 }, true)]
    [InlineData("List", true, "float32", TensorElementType.Float32, new[] { 2 }, false)]
    [InlineData("List", true, "float32", TensorElementType.Float32, new[] { 1, -1 }, false)]
    [InlineData("List", true, "list<float32,4>", TensorElementType.Float32, new[] { 1, 4 }, true)]
    [InlineData("List", true, "list<float32,4>", TensorElementType.Float32, new[] { -1, -1 }, true)]
    public void EachLayout_AcceptsItsShapes_WithFixedDimensionsAsTheirSizeOrAny(
        string layoutName, bool broadcast, string column, TensorElementType declaredType, int[] declaredShape, bool accepted)
    {
        var layout = Enum.Parse<TensorLayout>(layoutName);
        var inputs = new ModelTensorRule.MappedInput[] { new("x", broadcast ? "ctx.x" : "col.x", ColumnType.Parse(column), broadcast) };
        var output = layout == TensorLayout.List ? new[] { 1, -1 } : Rows;

        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", layout, inputs, new ModelTensorRule.MappedOutput[] { new("p", "/output") },
            Signature(new[] { new TensorSpec("x", declaredType, declaredShape) }, new[] { new TensorSpec("p", TensorElementType.Float32, output) }));

        if (accepted)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Equal(("/inputs/x", "MODEL_TENSOR_MISMATCH"), Assert.Single(problems.Select(p => (p.ConfigKey, p.Code))));
        }
    }

    [Theory]
    [InlineData("Pairs", new[] { -1 }, true)]
    [InlineData("Pairs", new[] { 256 }, false)]
    [InlineData("Pairs", new[] { -1, 1 }, false)]
    [InlineData("Pairs", new[] { -1, 2 }, false)]
    [InlineData("List", new[] { 1, -1 }, true)]
    [InlineData("List", new[] { -1, -1 }, true)]
    [InlineData("List", new[] { -1 }, false)]
    [InlineData("List", new[] { 1, 50 }, false)]
    [InlineData("List", new[] { 2, -1 }, false)]
    public void EachLayout_ReadsAFloatScoreOfItsShape_AndSaysWhatItReads(string layoutName, int[] shape, bool accepted)
    {
        var layout = Enum.Parse<TensorLayout>(layoutName);
        var inputs = new ModelTensorRule.MappedInput[] { new("x", "col.x", ColumnType.Float32) };
        var x = new TensorSpec("x", TensorElementType.Float32, layout == TensorLayout.List ? new[] { 1, -1 } : new[] { -1, 2 });

        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", layout, inputs, new ModelTensorRule.MappedOutput[] { new("p", "/output") },
            Signature(new[] { x }, new[] { new TensorSpec("p", TensorElementType.Float32, shape) }));

        Assert.Equal(accepted, ModelTensorRule.AcceptsScore(layout, new TensorSpec("p", TensorElementType.Float32, shape)));
        Assert.Equal(accepted, ModelTensorRule.AcceptsScore(layout, new TensorSpec("p", TensorElementType.Float64, shape)));
        Assert.False(ModelTensorRule.AcceptsScore(layout, new TensorSpec("p", TensorElementType.Int64, shape)));
        if (accepted)
        {
            Assert.Empty(problems);
            return;
        }

        var problem = Assert.Single(problems);
        Assert.Equal(("/output", "MODEL_TENSOR_UNSUPPORTED"), (problem.ConfigKey, problem.Code));
        Assert.Equal(ModelTensorRule.TensorHint(layout), problem.Hint);
        Assert.Contains(layout == TensorLayout.Pairs ? "reads the output as float32 or float64 [-1] (one value per pair" : "reads the output as float32 or float64 [1, -1]",
            problem.Hint);
    }

    [Theory]
    [InlineData("Pairs", false, "float32", TensorElementType.Float32, new[] { -1 }, "float32 [-1]", "float32 [-1, 2]",
        "or map a float32 context value instead (PairwiseRanker@1 sends a column's values of the two rows of each pair, and a context value once per pair)")]
    [InlineData("Pairs", false, "int64", TensorElementType.Float32, new[] { -1, 2 }, "float32 [-1, 2]", "int64 [-1, 2]",
        "or map a float32 derived feature of col.x instead")]
    [InlineData("Pairs", false, "list<float32,4>", TensorElementType.Float32, new[] { -1, 2, 8 }, "float32 [-1, 2, 8]", "float32 [-1, 2, 4]",
        "or map a list<float32,8> derived feature of col.x instead")]
    [InlineData("Pairs", true, "int64", TensorElementType.Int64, new[] { -1, 2, 3 }, "int64 [-1, 2, 3]", "int64 [-1]",
        "or map a list<int64,3> column instead (PairwiseRanker@1 sends a column's values of the two rows of each pair, and a context value once per pair)")]
    [InlineData("Pairs", true, "int64", TensorElementType.Int64, new[] { -1, 2, 3, 4 }, "int64 [-1, 2, 3, 4]", "int64 [-1]",
        "no column gives int64 [-1, 2, 3, 4]")]
    [InlineData("List", true, "float32", TensorElementType.Int64, new[] { 1, -1 }, "int64 [1, -1]", "float32 [1]",
        "or map a list<int64,N> derived feature of ctx.x instead")]
    [InlineData("List", true, "float32", TensorElementType.Int64, new[] { 1, -1, 3 }, "int64 [1, -1, 3]", "float32 [1]",
        "or map a list<int64,3> column instead (ListwiseReranker@1 sends a column's values of the rows of the list, and a context value once per list)")]
    // Same kind first: a context list whose N is declared -1 gives [1, -1] too.
    [InlineData("List", true, "float32", TensorElementType.Float32, new[] { 1, -1 }, "float32 [1, -1]", "float32 [1]",
        "or map a list<float32,N> derived feature of ctx.x instead")]
    [InlineData("List", false, "float32", TensorElementType.Float32, new[] { 1 }, "float32 [1]", "float32 [1, -1]",
        "or map a float32 context value instead")]
    [InlineData("List", false, "float32", TensorElementType.Int64, new[] { 1, -1 }, "int64 [1, -1]", "float32 [1, -1]",
        "or map an int64 derived feature of col.x instead")]
    public void AMismatchInAPairOrListLayout_SaysWhatTheOperatorSends_AndWhichReferenceWouldGiveTheDeclaredTensor(
        string layoutName, bool broadcast, string column, TensorElementType declaredType, int[] declaredShape, string declared, string sent, string hint)
    {
        var layout = Enum.Parse<TensorLayout>(layoutName);
        var source = broadcast ? "ctx.x" : "col.x";
        var inputs = new ModelTensorRule.MappedInput[] { new("x", source, ColumnType.Parse(column), broadcast) };
        var output = layout == TensorLayout.List ? new[] { 1, -1 } : Rows;

        var problems = ModelTensorRule.Check("Model 'm' version 'v2'", layout, inputs, new ModelTensorRule.MappedOutput[] { new("p", "/output") },
            Signature(new[] { new TensorSpec("x", declaredType, declaredShape) }, new[] { new TensorSpec("p", TensorElementType.Float32, output) }));

        var problem = Assert.Single(problems);
        Assert.Equal(
            $"Model 'm' version 'v2' declares input 'x' as {declared}, but it maps {source} ({column}), which {ModelTensorRule.OperatorId(layout)} sends as {sent}.",
            problem.Message);
        Assert.StartsWith($"declare input 'x' as {sent} in the model's signature", problem.Hint);
        Assert.Contains(hint, problem.Hint);
    }

    private static TensorSpec Float(string name) => new(name, TensorElementType.Float32, Rows);

    private static TensorSpec Spec(string name, TensorElementType type, params int[] shape) => new(name, type, shape);

    private static ServiceSignature Signature(TensorSpec[] inputs, TensorSpec[] outputs) => new(inputs, outputs);
}
