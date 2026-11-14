using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Expressions;
using StrataRec.Expressions.Vectorized;

namespace StrataRec.UnitTests.Expressions;

/// <summary>Parsing, typing, diagnostics and explain output.</summary>
public class CompilerTests
{
    /// <summary>The home-feed catalog of design 6.3 plus the columns used in 7.3, 7.8 and 9.3.</summary>
    internal static ExprFixture HomeFeed()
    {
        var f = new ExprFixture()
            .Col("col.item.clicks_7d", "float32")
            .Col("col.item.impressions_7d", "float32")
            .Col("col.item.engagements_30d", "float32")
            .Col("col.item.bookmarks_7d", "float32")
            .Col("col.item.report_rate", "float32")
            .Col("col.item.age_hours", "float32?")
            .Col("col.item.is_policy_safe", "bool?")
            .Col("col.item.embedding", "vec<float32,64>?")
            .Col("col.item.creator_id", "int64")
            .Col("col.creator.follower_count", "float32")
            .Col("col.score.p_click", "float64")
            .Col("col.score.p_like", "float64")
            .Col("col.score.coarse", "float64")
            .Col("col.score.final", "float64")
            .Version("col.score.final@fusion", "float64")
            .Col("col.recall.best_score", "float64")
            .Col("col.recall.primary_source", "string")
            .Ctx("ctx.user.embedding", "vec<float32,64>?")
            .Ctx("ctx.user.tenure_days", "float32", ScalarValue.FromFloat64(45))
            .Ctx("ctx.request.recent_item_ids", "list<int64>", ScalarValue.FromInt64List(new long[] { 1001, 1003 }))
            .Ctx("ctx.request.seed", "int64", ScalarValue.FromInt64(42))
            .Ctx("ctx.request.surface", "string", ScalarValue.FromString("home"))
            .Ctx("ctx.request.country", "string", ScalarValue.FromString("NZ"))
            .Ctx("ctx.request.user_id", "int64", ScalarValue.FromInt64(7))
            .Ctx("ctx.request.id", "string", ScalarValue.FromString("r-1"))
            .Param("param.w_click", "float64", ScalarValue.FromFloat64(1.0))
            .Param("param.w_like", "float64", ScalarValue.FromFloat64(0.5))
            .Param("param.w_fresh", "float64", ScalarValue.FromFloat64(0.2))
            .Param("param.half_life_hours", "float64", ScalarValue.FromFloat64(48))
            .Param("param.cold_creator_boost", "float64", ScalarValue.FromFloat64(1.3))
            .Param("param.explore_eps", "float64", ScalarValue.FromFloat64(0.01))
            .Param("param.recall_quotas.cf", "int64", ScalarValue.FromInt64(200));
        return f.Build(8);
    }

    internal const string FusionFormula = """
        let utility    = param.w_click * col.score.p_click + param.w_like * col.score.p_like;
        let age_hours  = col.item.age_hours ?? 720.0;
        let freshness  = exp(-age_hours / param.half_life_hours);
        let cold_boost = (col.creator.follower_count < 1000.0 && ctx.user.tenure_days > 30.0) ? param.cold_creator_boost : 1.0;
        utility * (1.0 + param.w_fresh * freshness) * cold_boost
        """;

    public static TheoryData<string, string> DesignExamples() => new()
    {
        // design 6.3 derived features
        { "(col.item.clicks_7d + 10.0) / (col.item.impressions_7d + 100.0)", "float64" },
        { "dot(ctx.user.embedding, col.item.embedding) ?? 0.0", "float64" },
        // design 7.3
        { FusionFormula, "float64" },
        { "!contains(ctx.request.recent_item_ids, col.entity_id) && (col.item.is_policy_safe ?? false)", "bool" },
        // design 7.8
        { "param.w_click * col.score.p_click + param.w_like * col.score.p_like", "float64" },
        { "1.0 + param.w_fresh * exp(-(col.item.age_hours ?? 720.0) / param.half_life_hours)", "float64" },
        { "col.item.report_rate > 0.05 ? 0.2 : 1.0", "float64" },
        { "param.explore_eps * hash01(ctx.request.seed, col.entity_id)", "float64" },
        // design 8.3, 9.3, 11.3
        { "col.score.coarse > 0.35", "bool" },
        { "col.item.is_policy_safe ?? false", "bool" },
        { "col.recall.best_score * (1.0 + 0.05 * log1p(col.item.clicks_7d))", "float64" },
        { "col.score.final * (1.0 + 0.15 * exp(-(col.item.age_hours ?? 720.0) / 24.0))", "float64" },
        { "col.item.bookmarks_7d / (col.item.clicks_7d + 1.0)", "float64" },
        { "(col.item.is_policy_safe ?? false) && col.item.report_rate < 0.05", "bool" },
        // design 4.4 / 7.2
        { "col.recall.primary_source == 'two_tower'", "bool" },
        { "col.score.final@fusion * 2.0", "float64" },
    };

    [Theory]
    [MemberData(nameof(DesignExamples))]
    public void DesignExamples_Compile_AsNonNullable(string source, string expected)
    {
        var f = HomeFeed();
        var options = expected == "bool" ? CompileOptions.Predicate : CompileOptions.Score;
        var compiled = ExpressionCompiler.Compile(source, f.Scope, options);
        Assert.Equal(ColumnType.Parse(expected), compiled.ResultType);
        Assert.True(compiled.IsRowDependent);
    }

    [Theory]
    [InlineData("ctx.request.surface == 'home'")]
    [InlineData("ctx.request.country in ['NZ', 'AU'] && param.recall_quotas.cf > 100")]
    [InlineData("ctx.request.surface.startsWith('ho')")]
    public void RequestConditions_Compile_AndEvaluateOnce(string source)
    {
        var f = HomeFeed();
        var compiled = ExpressionCompiler.Compile(source, f.Scope, CompileOptions.RequestCondition);
        Assert.False(compiled.IsRowDependent);
        Assert.Equal(ScalarValue.FromBool(true), compiled.EvaluateScalar(f.RequestContext));
    }

    [Fact]
    public void FusionFormula_Reads_And_Hoists()
    {
        var f = HomeFeed();
        var compiled = (VectorizedExpression)ExpressionCompiler.Compile(FusionFormula, f.Scope, CompileOptions.Score);
        var reads = compiled.Reads.Select(s => s.Name).ToHashSet();
        Assert.Contains("col.score.p_click", reads);
        Assert.Contains("ctx.user.tenure_days", reads);
        Assert.Contains("param.half_life_hours", reads);
        Assert.Equal(compiled.Reads.Count, reads.Count);

        // ctx.user.tenure_days > 30.0 and -param.half_life_hours are evaluated once per call, not per row.
        Assert.True(compiled.Program!.HoistedCount >= 8);
        Assert.DoesNotContain(compiled.Program.Instructions, i => i is ColumnInstr c && c.Symbol.Kind != SymbolKind.Column);
        Assert.InRange(compiled.Program.Instructions.Count, 10, 20);
    }

    [Theory]
    [InlineData("1 // comment\n + 2", 3L)]
    [InlineData("0x10 + 1", 17L)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("- -3", 3L)]
    [InlineData("size('''a\nb''' == 'a\\nb' ? [1, 2] : [3])", 2L)]
    [InlineData("size(\"\\u00e9\\x41\\101\" == 'éAA' ? [1] : [1, 2])", 1L)]
    [InlineData("size(r'\\n' == '\\\\n' ? [1] : [1, 2])", 1L)]
    [InlineData("[1, 2, 3,].size()", 3L)]
    [InlineData("7 % 3 * 2", 2L)]
    [InlineData("1 + 2 * 3 - 4", 3L)]
    public void Literals_And_Syntax(string source, long expected) =>
        Assert.Equal(ScalarValue.FromInt64(expected), new ExprFixture().Build(1).Scalar(source));

    [Theory]
    [InlineData(".5 + 1e-3", 0.501)]
    [InlineData("1.5e2", 150.0)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("min(3, 1.5, 2)", 1.5)]
    [InlineData("max(1, 2)", 2.0)]
    [InlineData("clamp(5, 0, 1.0)", 1.0)]
    [InlineData("clamp(0.5, 1.0, 0.0)", 0.0)]
    [InlineData("'abc'.startsWith('ab') ? 1.0 : 0.0", 1.0)]
    [InlineData("let a = 2.0; let b = a * a; b + a", 6.0)]
    [InlineData("(let a = 1.0; a + 1.0) * 3.0", 6.0)]
    [InlineData("true ? 1 : 2.5", 1.0)]
    public void FloatExpressions(string source, double expected)
    {
        var value = new ExprFixture().Build(1).Scalar(source);
        Assert.Equal(expected, value.AsFloat64(), 12);
    }

    [Fact]
    public void Coalesce_Precedence_IsBetweenOrAndConditional_AndRightAssociative()
    {
        var f = new ExprFixture()
            .Ctx("ctx.a", "bool?")
            .Ctx("ctx.b", "bool?", ScalarValue.FromBool(false))
            .Ctx("ctx.x", "float64?")
            .Ctx("ctx.y", "float64?")
            .Build(1);

        // (a || b) ?? true: missing || false = missing → true. The other grouping would give missing.
        Assert.Equal(ScalarValue.FromBool(true), f.Scalar("ctx.a || ctx.b ?? true"));

        // (a ?? true) ? 'big' : 'small'
        Assert.Equal(ScalarValue.FromString("big"), f.Scalar("ctx.a ?? true ? 'big' : 'small'"));

        // Like C#, '??' binds looser than comparisons: x ?? (2.0 > 1.0) is a type error.
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, Assert.Single(f.Errors("ctx.x ?? 2.0 > 1.0")).Code);

        // x ?? (y ?? 3.0)
        Assert.Equal(3.0, f.Scalar("ctx.x ?? ctx.y ?? 3.0").AsFloat64());
        f.SetCtx("ctx.y", ScalarValue.FromFloat64(5));
        Assert.Equal(5.0, f.Scalar("ctx.x ?? ctx.y ?? 3.0").AsFloat64());
        Assert.Equal("ctx.x ?? ctx.y ?? 3.0", f.Compile("ctx.x ?? ctx.y ?? 3.0").Describe());
        Assert.Equal("(ctx.x ?? ctx.y) ?? 3.0", f.Compile("(ctx.x ?? ctx.y) ?? 3.0").Describe());
    }

    [Fact]
    public void MethodCallSyntax_IsSugarForFunctionCalls()
    {
        var f = new ExprFixture()
            .Col("col.tags", "list<string>")
            .Col("col.name", "string")
            .Ctx("ctx.request.surface", "string", ScalarValue.FromString("home_feed"))
            .Build(2);
        f.Buffer<StrataRec.Core.Columns.ListColumn<string>>("col.tags").Set(0, new[] { "a", "b" });
        f.Buffer<StrataRec.Core.Columns.ListColumn<string>>("col.tags").Set(1, new[] { "c" });
        f.FillStrings("col.name", "alpha", "beta");

        Assert.Equal("size(col.tags)", f.Compile("col.tags.size()").Describe());
        Assert.Equal(new[] { 2L, 1L }, f.Rows("col.tags.size()").Select(v => v.AsInt64()));
        Assert.Equal(new[] { true, false }, f.Rows("col.name.startsWith('al')").Select(v => v.AsBool()));
        Assert.Equal(new[] { true, false }, f.Rows("col.tags.contains('b')").Select(v => v.AsBool()));
        Assert.True(f.Scalar("ctx.request.surface.startsWith('ho')").AsBool());
    }

    [Fact]
    public void UnknownName_SuggestsClosestName()
    {
        var f = HomeFeed();
        var d = Assert.Single(f.Errors("col.item.clikcs_7d + 1.0"));
        Assert.Equal(ExpressionErrorCodes.UnknownName, d.Code);
        Assert.Equal(0, d.Offset);
        Assert.Equal("col.item.clikcs_7d".Length, d.Length);
        Assert.Contains("'col.item.clicks_7d'", d.Hint);

        d = Assert.Single(f.Errors("1.0 + item.clicks_7d"));
        Assert.Equal(ExpressionErrorCodes.UnknownName, d.Code);
        Assert.Equal(6, d.Offset);
        Assert.Contains("'col.item.clicks_7d'", d.Hint);

        d = Assert.Single(f.Errors("let base = 1.0; bsae * 2.0"));
        Assert.Contains("'base'", d.Hint);
    }

    [Fact]
    public void MultipleErrors_AreCollected()
    {
        var errors = HomeFeed().Errors("col.foo + col.bar * sigmod(1.0)");
        Assert.Equal(new[] { ExpressionErrorCodes.UnknownName, ExpressionErrorCodes.UnknownName, ExpressionErrorCodes.UnknownFunction }, errors.Select(e => e.Code));
        Assert.Equal(new[] { 0, 10, 20 }, errors.Select(e => e.Offset));
        Assert.Contains("'sigmoid'", errors[2].Hint);
    }

    [Theory]
    [InlineData("pow(1.0)", ExpressionErrorCodes.ArgCount, 0)]
    [InlineData("soft_floor(1.0, 2.0, 3.0, 4.0)", ExpressionErrorCodes.ArgCount, 0)]
    [InlineData("1.0 + col.item.unknown.size()", ExpressionErrorCodes.UnknownName, 6)]
    [InlineData("col.item.clicks_7d.frobnicate()", ExpressionErrorCodes.UnknownFunction, 19)]
    [InlineData("col.item.clicks_7d && true", ExpressionErrorCodes.TypeMismatch, 0)]
    [InlineData("5.0 % 2.0", ExpressionErrorCodes.TypeMismatch, 4)] // both operands wrong: the operator
    [InlineData("'a' < 'b'", ExpressionErrorCodes.TypeMismatch, 4)]
    [InlineData("col.recall.primary_source + 'x'", ExpressionErrorCodes.TypeMismatch, 26)]
    [InlineData("dot(col.item.embedding, ctx.small)", ExpressionErrorCodes.TypeMismatch, 24)] // the second vector disagrees
    [InlineData("1.5 in col.tags_int", ExpressionErrorCodes.TypeMismatch, 0)]
    [InlineData("size(col.recall.primary_source)", ExpressionErrorCodes.TypeMismatch, 5)]
    [InlineData("ctx.request.seed@fusion", ExpressionErrorCodes.UnknownName, 0)]
    [InlineData("let col = 1.0; col", ExpressionErrorCodes.InvalidLet, 4)]
    [InlineData("let size = 1.0; size", ExpressionErrorCodes.InvalidLet, 4)]
    [InlineData("let a = 1.0; let a = 2.0; a", ExpressionErrorCodes.InvalidLet, 17)]
    [InlineData("piecewise_linear(1.0, [0.0, 2.0, 1.0], [0.0, 1.0, 2.0])", ExpressionErrorCodes.InvalidArgument, 22)]
    [InlineData("piecewise_linear(1.0, [0.0, 1.0], [0.0, 1.0, 2.0])", ExpressionErrorCodes.InvalidArgument, 34)]
    [InlineData("piecewise_linear(1.0, ctx.knots, [0.0, 1.0])", ExpressionErrorCodes.TypeMismatch, 22)]
    [InlineData("lookup('nope', 1, 0.0)", ExpressionErrorCodes.UnknownTable, 7)]
    [InlineData("lookup(ctx.request.surface, 1, 0.0)", ExpressionErrorCodes.TypeMismatch, 7)]
    [InlineData("[1.0, 2.0]", ExpressionErrorCodes.TypeMismatch, 0)]
    [InlineData("[]", ExpressionErrorCodes.TypeMismatch, 0)]
    [InlineData("[col.score.final]", ExpressionErrorCodes.TypeMismatch, 1)]
    public void ErrorCodes_And_Offsets(string source, string code, int offset)
    {
        var f = HomeFeed();
        f.Scope.Add(new Symbol(SymbolKind.Context, "ctx.small", ColumnType.Parse("vec<float32,8>"), 99));
        f.Scope.Add(new Symbol(SymbolKind.Context, "ctx.knots", ColumnType.ListInt64, 98));
        f.Scope.Add(new Symbol(SymbolKind.Column, "col.tags_int", ColumnType.ListInt64, 97));
        var d = f.Errors(source)[0];
        Assert.Equal(code, d.Code);
        Assert.Equal(offset, d.Offset);
        Assert.True(d.Length >= 1);
    }

    [Theory]
    [InlineData("1 +", 3, null)]
    [InlineData("(1 + 2", 6, null)]
    [InlineData("'abc", 0, "close the string")]
    [InlineData("a = 1", 2, "'=='")]
    [InlineData("null", 0, "has(x)")]
    [InlineData("ctx.request.surface[0]", 19, "contains()")]
    [InlineData("1 & 2", 2, "'&&'")]
    [InlineData("{'a': 1}", 0, null)]
    [InlineData("1u", 0, "drop the 'u'")]
    [InlineData("9223372036854775808", 0, null)]
    [InlineData("'\\q'", 1, null)]
    [InlineData("col.score.final@", 15, "@fusion")]
    [InlineData("(1 + 2).foo", 8, null)]
    public void SyntaxErrors_HaveOffsets_AndHints(string source, int offset, string? hint)
    {
        var d = Assert.Single(HomeFeed().Errors(source));
        Assert.Equal(ExpressionErrorCodes.Syntax, d.Code);
        Assert.Equal(offset, d.Offset);
        if (hint is not null)
        {
            Assert.Contains(hint, d.Hint);
        }
    }

    [Fact]
    public void NullableResult_PointsAtTheMissingInput_WithFixHint()
    {
        var f = HomeFeed();
        var d = Assert.Single(f.Errors("col.item.age_hours / 24.0", CompileOptions.Score));
        Assert.Equal(ExpressionErrorCodes.NullableResult, d.Code);
        Assert.Equal(0, d.Offset);
        Assert.Equal("col.item.age_hours".Length, d.Length);
        Assert.Contains("col.item.age_hours ?? 0.0", d.Hint);
        Assert.Contains("?? 0.0", d.Hint);

        d = Assert.Single(f.Errors("let fresh = exp(-col.item.age_hours / 24.0); 1.0 + fresh", CompileOptions.Score));
        Assert.Equal(17, d.Offset);

        d = Assert.Single(f.Errors("col.item.is_policy_safe && col.score.coarse > 0.5", CompileOptions.Predicate));
        Assert.Contains("col.item.is_policy_safe ?? false", d.Hint);

        // Resolved nullability compiles; nullable sites accept it.
        Assert.NotNull(f.Compile("(col.item.age_hours ?? 0.0) / 24.0", CompileOptions.Score));
        Assert.True(f.Compile("col.item.age_hours / 24.0", new CompileOptions { ExpectedType = ColumnType.Float32, AllowNullableResult = true }).ResultType.Nullable);
    }

    [Theory]
    [InlineData("col.score.final > 0.5", "float64", "write 'cond ? 1.0 : 0.0'")]
    [InlineData("col.score.final", "bool", "comparison")]
    [InlineData("col.score.final", "int64", "integer")]
    [InlineData("col.recall.primary_source", "float32", null)]
    public void ResultType_IsCheckedAgainstTheSite(string source, string expected, string? hint)
    {
        var d = Assert.Single(HomeFeed().Errors(source, new CompileOptions { ExpectedType = ColumnType.Parse(expected) }));
        Assert.Equal(ExpressionErrorCodes.TypeMismatch, d.Code);
        if (hint is not null)
        {
            Assert.Contains(hint, d.Hint);
        }
    }

    [Fact]
    public void IntResults_AreAcceptedForFloatSites()
    {
        var f = HomeFeed();
        var compiled = f.Compile("col.item.creator_id % 10", new CompileOptions { ExpectedType = ColumnType.Float32 });
        Assert.Equal(ColumnType.Int64, compiled.ResultType);
    }

    [Fact]
    public void RowReference_InRequestLevelSite_IsReported()
    {
        var d = Assert.Single(HomeFeed().Errors("ctx.request.surface == 'home' && col.score.final > 0.5", CompileOptions.RequestCondition));
        Assert.Equal(ExpressionErrorCodes.RowReferenceInRequestLevel, d.Code);
        Assert.Equal(33, d.Offset);
        Assert.Equal("col.score.final".Length, d.Length);
        Assert.Contains("ctx.*", d.Hint);
    }

    [Theory]
    [InlineData("abs(", ")", 32)]
    [InlineData("(", ")", 32)]
    [InlineData("-(", ")", 16)]
    [InlineData("[", "]", 32)]
    public void Depth_32_Compiles_33_IsRejected(string open, string close, int count)
    {
        var f = new ExprFixture().Build(1);
        string Nest(int n) => string.Concat(Enumerable.Repeat(open, n)) + "1" + string.Concat(Enumerable.Repeat(close, n));
        if (open == "[")
        {
            // A list of lists is a type error, but depth is checked first while parsing.
            Assert.DoesNotContain(f.Errors(Nest(count)), d => d.Code == ExpressionErrorCodes.TooDeep);
        }
        else
        {
            Assert.True(ExpressionCompiler.TryCompile(Nest(count), f.Scope, CompileOptions.Any, out _, out var ok), string.Join("; ", ok));
        }

        Assert.Equal(ExpressionErrorCodes.TooDeep, Assert.Single(f.Errors(Nest(count + 1))).Code);
    }

    [Fact]
    public void Depth_CountsLeftAssociativeChains()
    {
        var f = new ExprFixture().Build(1);
        var ok = string.Join(" + ", Enumerable.Repeat("1", 33));      // 32 operators
        var tooDeep = string.Join(" + ", Enumerable.Repeat("1", 34)); // 33 operators
        Assert.Equal(33L, f.Scalar(ok).AsInt64());
        Assert.Equal(ExpressionErrorCodes.TooDeep, Assert.Single(f.Errors(tooDeep)).Code);
        Assert.Equal(ExpressionErrorCodes.TooDeep, Assert.Single(f.Errors(tooDeep, new CompileOptions { MaxDepth = 32, AllowNullableResult = true })).Code);
        Assert.Equal(34L, f.Compile(tooDeep, new CompileOptions { MaxDepth = 40 }).EvaluateScalar(f.Context).AsInt64());
    }

    [Fact]
    public void Depth_IsCheckedBeforeRecursing_SoHugeInputsDoNotOverflowTheStack()
    {
        var f = new ExprFixture().Build(1);
        var deep = new string('(', 30_000) + "1" + new string(')', 30_000);
        var d = Assert.Single(f.Errors(deep));
        Assert.Equal(ExpressionErrorCodes.TooDeep, d.Code);
        Assert.Equal(32, d.Offset);

        var unary = new string('!', 60_000) + "true";
        Assert.Equal(ExpressionErrorCodes.TooDeep, Assert.Single(f.Errors(unary)).Code);

        var tooLong = string.Join(" + ", Enumerable.Repeat("1", 30_000));
        Assert.Equal(ExpressionErrorCodes.TooLong, Assert.Single(f.Errors(tooLong)).Code);
    }

    [Fact]
    public void Depth_AfterLetExpansion_IsBounded()
    {
        var f = new ExprFixture().Col("col.x", "float64").Build(1);
        var src = new System.Text.StringBuilder("let v0 = col.x;");
        for (var i = 1; i < 300; i++)
        {
            src.Append($" let v{i} = v{i - 1} * 1.5 + 1.0;");
        }

        src.Append(" v299");
        var d = Assert.Single(f.Errors(src.ToString()));
        Assert.Equal(ExpressionErrorCodes.TooDeep, d.Code);
    }

    [Fact]
    public void Describe_PrintsOptimizedForm_WithSharedSubexpressionsAsLets()
    {
        var f = new ExprFixture().Col("col.x", "float64").Col("col.n", "float64?").Param("param.w", "float64", ScalarValue.FromFloat64(2)).Build(1);
        Assert.Equal("3.0 * col.x", f.Compile("(1.0 + 2.0) * col.x").Describe());
        Assert.Equal("let t1 = exp(col.x);\nt1 + t1", f.Compile("exp(col.x) + exp(col.x)").Describe());
        Assert.Equal("let a = col.x * param.w;\na + a / 2.0", f.Compile("let a = col.x * param.w; a + a / 2.0").Describe());
        Assert.Equal("col.x", f.Compile("has(col.x) ? col.x * 1.0 : 0.0").Describe());
        Assert.Equal("col.x / -param.w", f.Compile("-col.x / param.w").Describe());
        Assert.Equal("(col.n ?? 1.0) > 0.5 && col.x in [1.0, 2.0]", f.Compile("(col.n ?? 1.0) > 0.5 && col.x in [1, 2]").Describe());
        Assert.Equal("false", f.Compile("false && col.n > 1.0").Describe());
        Assert.Equal("col.n > 1.0", f.Compile("true && col.n > 1.0").Describe());
        Assert.Equal("(1.0 / 0.0)", f.Compile("1.0 / 0.0").Describe());
        Assert.Equal("'it\\'s'", f.Compile("\"it's\"").Describe());
    }

    [Fact]
    public void Optimizer_KeepsIntegerModuloByZero_ForCounting()
    {
        var f = new ExprFixture().Build(1);
        Assert.Equal("5 % 0", f.Compile("5 % 0").Describe());
        Assert.Equal("2", f.Compile("5 % 3").Describe());
    }

    [Fact]
    public void Reads_AreDeduplicated_AndReflectTheOptimizedExpression()
    {
        var f = HomeFeed();
        var compiled = f.Compile("col.score.final + col.score.final * param.w_click + (false && col.score.coarse > 1.0 ? 1.0 : 0.0)");
        Assert.Equal(new[] { "col.score.final", "param.w_click" }, compiled.Reads.Select(s => s.Name).OrderBy(s => s, StringComparer.Ordinal));

        var request = f.Compile("param.w_click * 2.0");
        Assert.False(request.IsRowDependent);
        Assert.Throws<InvalidOperationException>(() => f.Compile("col.score.final").EvaluateScalar(f.Context));
    }

    [Fact]
    public void Compile_Throws_WithAllDiagnostics()
    {
        var e = Assert.Throws<ExpressionCompileException>(() => ExpressionCompiler.Compile("col.a + col.b", HomeFeed().Scope));
        Assert.Equal(2, e.Diagnostics.Count);
        Assert.Contains("EXPR_UNKNOWN_NAME", e.Message);
        Assert.Equal("col.a + col.b", e.Source);
    }
}
