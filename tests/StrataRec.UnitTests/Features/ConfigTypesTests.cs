using StrataRec.Core.Types;
using StrataRec.Features.Catalog;

namespace StrataRec.UnitTests.Features;

/// <summary>
/// <see cref="ConfigTypes"/>: a type a place does not accept is answered with the spelling meant (a typo, another case,
/// another language's name) and the spellings valid there.
/// </summary>
public class ConfigTypesTests
{
    [Theory]
    [InlineData("int46", TypeSite.Column, "int64")]
    [InlineData("int46?", TypeSite.Column, "int64?")] // a column keeps its '?'
    [InlineData("int46", TypeSite.Parameter, "int64")]
    [InlineData("Int64", TypeSite.Column, "int64")] // only the case differs
    [InlineData("long", TypeSite.Column, "int64")]
    [InlineData("integer?", TypeSite.Column, "int64?")]
    [InlineData("short", TypeSite.Column, "int32")]
    [InlineData("float", TypeSite.Column, "float32")]
    [InlineData("float", TypeSite.Parameter, "float64")] // a parameter has no float32
    [InlineData("double", TypeSite.Parameter, "float64")]
    [InlineData("boolean", TypeSite.Feature, "bool")]
    [InlineData("str", TypeSite.Column, "string")]
    [InlineData("strng", TypeSite.Parameter, "string")]
    [InlineData("flaot64", TypeSite.Feature, "float64")]
    [InlineData("vec<float64,4>", TypeSite.Column, "vec<float32,4>")] // the dimension written is kept
    [InlineData("vector", TypeSite.Feature, "vec<float32,N>")]
    public void AnUnknownType_SuggestsTheSpellingMeant_AndListsTheValidOnes(string written, TypeSite site, string suggestion)
    {
        Assert.False(ConfigTypes.TryParse(written, site, out _, out _, out var message, out var hint));

        Assert.Equal(site == TypeSite.Parameter ? $"Unknown parameter type '{written}'." : $"Unknown type '{written}'.", message);
        Assert.Equal($"did you mean '{suggestion}'? Valid types: {ConfigTypes.Spellings(site)}", hint);
    }

    [Fact]
    public void AnUnknownType_WithNothingClose_ListsTheValidOnes()
    {
        Assert.False(ConfigTypes.TryParse("xyzzy", TypeSite.Column, out _, out _, out _, out var hint));

        Assert.Equal("valid types: " + ConfigTypes.Spellings(TypeSite.Column), hint);
    }

    [Fact]
    public void AMapParameter_SuggestsTheWholeMap()
    {
        Assert.False(ConfigTypes.TryParse("map<string,int46>", TypeSite.Parameter, out _, out _, out _, out var hint));

        Assert.StartsWith("did you mean 'map<string,int64>'?", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("int32", "'int32' is not a parameter type.", "write 'int64'; parameter types: ")]
    [InlineData("float32", "'float32' is not a parameter type.", "write 'float64'; parameter types: ")]
    [InlineData("map<string,int32>", "'int32' is not a parameter type.", "write 'map<string,int64>'; parameter types: ")]
    [InlineData("list<int64>", "'list<int64>' is not a parameter type.", "parameter types: ")]
    [InlineData("int64?", "Parameter types are written without '?': a parameter always has a value (got 'int64?').", "write 'int64'")]
    [InlineData("map<int64,float64>", "Map parameters have string keys, got 'map<int64,float64>'.", "write map<string,T> with T one of ")]
    public void AParameter_TakesItsOwnTypes(string written, string message, string hint)
    {
        Assert.False(ConfigTypes.TryParse(written, TypeSite.Parameter, out _, out _, out var actualMessage, out var actualHint));

        Assert.Equal(message, actualMessage);
        Assert.StartsWith(hint, actualHint, StringComparison.Ordinal);
    }

    [Fact]
    public void AFeature_IsNullableByItsDefault_NotByAQuestionMark()
    {
        Assert.False(ConfigTypes.TryParse("float32?", TypeSite.Feature, out _, out _, out var message, out var hint));

        Assert.Equal("Feature types are written without '?'; nullability comes from 'default' (got 'float32?').", message);
        Assert.Equal("remove the '?' and omit 'default' to make the feature nullable", hint);
    }

    [Fact]
    public void AnEmptyType_ListsTheValidOnes()
    {
        Assert.False(ConfigTypes.TryParse("  ", TypeSite.Column, out _, out _, out var message, out var hint));

        Assert.Equal("The type is empty.", message);
        Assert.Equal("write one of: " + ConfigTypes.Spellings(TypeSite.Column), hint);
    }

    [Theory]
    [InlineData("int64?", TypeSite.Column, "int64?", false)]
    [InlineData("vec<float32,8>", TypeSite.Feature, "vec<float32,8>", false)]
    [InlineData("float64", TypeSite.Parameter, "float64", false)]
    [InlineData("map<string,float64>", TypeSite.Parameter, "float64", true)]
    public void AValidType_Parses(string written, TypeSite site, string type, bool map)
    {
        Assert.True(ConfigTypes.TryParse(written, site, out var parsed, out var isMap, out _, out _));

        Assert.Equal(ColumnType.Parse(type), parsed);
        Assert.Equal(map, isMap);
    }
}
