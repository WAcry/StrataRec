using StrataRec.Core.Types;
using StrataRec.Features.Catalog;

namespace StrataRec.UnitTests.Features;

public class ConfigTypesTests
{
    [Theory]
    [InlineData("int46", TypeSite.Column, "int64")]
    [InlineData("int46?", TypeSite.Column, "int64?")]
    [InlineData("int46", TypeSite.Parameter, "int64")]
    [InlineData("Int64", TypeSite.Column, "int64")]
    [InlineData("long", TypeSite.Column, "int64")]
    [InlineData("integer?", TypeSite.Column, "int64?")]
    [InlineData("short", TypeSite.Column, "int32")]
    [InlineData("float", TypeSite.Column, "float32")]
    [InlineData("float", TypeSite.Parameter, "float64")]
    [InlineData("double", TypeSite.Parameter, "float64")]
    [InlineData("boolean", TypeSite.Feature, "bool")]
    [InlineData("str", TypeSite.Column, "string")]
    [InlineData("strng", TypeSite.Parameter, "string")]
    [InlineData("flaot64", TypeSite.Feature, "float64")]
    [InlineData("array<double,64>", TypeSite.Column, "list<float64,64>")]
    [InlineData("array<float,8>", TypeSite.Feature, "list<float32,8>")]
    public void AnUnknownType_SuggestsTheSpellingMeant_AndListsTheValidOnes(string written, TypeSite site, string suggestion)
    {
        Assert.False(ConfigTypes.TryParse(written, site, out _, out _, out var message, out var hint));

        Assert.Equal(site == TypeSite.Parameter ? $"Unknown parameter type '{written}'." : $"Unknown type '{written}'.", message);
        Assert.Equal($"did you mean '{suggestion}'? Valid types: {ConfigTypes.Spellings(site)}", hint);
    }

    [Theory]
    [InlineData("vec<float32,4>", TypeSite.Column, "list<float32,4>")]
    [InlineData("vec<float32,4>?", TypeSite.Column, "list<float32,4>?")]
    [InlineData("vec<float64,64>", TypeSite.Feature, "list<float64,64>")]
    [InlineData("vec<float32,16>", TypeSite.Feature, "list<float32,16>")]
    [InlineData("vector", TypeSite.Feature, "list<float32,N>")]
    public void TheRemovedVectorType_IsAnError_WhoseHintIsTheFixedLengthList(string written, TypeSite site, string replacement)
    {
        Assert.False(ConfigTypes.TryParse(written, site, out _, out _, out var message, out var hint));

        Assert.Equal($"Unknown type '{written}': a vector is a list of fixed length, list<E,N>.", message);
        Assert.Equal($"did you mean '{replacement}'? Valid types: {ConfigTypes.Spellings(site)}", hint);
    }

    [Theory]
    [InlineData("text", TypeSite.Column, "string")]
    [InlineData("text?", TypeSite.Column, "string?")]
    [InlineData("list<text>?", TypeSite.Column, "list<string>?")]
    [InlineData("text", TypeSite.Feature, "string")]
    [InlineData("list<text>", TypeSite.Feature, "list<string>")]
    [InlineData("list<text,3>", TypeSite.Feature, "list<string,3>")]
    [InlineData("text", TypeSite.Parameter, "string")]
    [InlineData("map<string,text>", TypeSite.Parameter, "map<string,string>")]
    public void TheRemovedTextType_IsAnError_WhoseHintIsString(string written, TypeSite site, string replacement)
    {
        Assert.False(ConfigTypes.TryParse(written, site, out _, out _, out var message, out var hint));

        var what = site == TypeSite.Parameter ? "Unknown parameter type" : "Unknown type";
        Assert.Equal($"{what} '{written}': there is one string type, string, for strings of any length (a category, a title, a prompt).", message);
        Assert.Equal($"did you mean '{replacement}'? Valid types: {ConfigTypes.Spellings(site)}", hint);
        Assert.DoesNotContain("text", ConfigTypes.Spellings(site), StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorParameter_IsAnError_ThatSaysParametersAreNotLists()
    {
        Assert.False(ConfigTypes.TryParse("vec<float32,4>", TypeSite.Parameter, out _, out _, out var message, out var hint));

        Assert.Equal("Unknown parameter type 'vec<float32,4>': a vector is a list of fixed length, list<E,N>.", message);
        Assert.StartsWith("a parameter is never a list; parameter types: ", hint, StringComparison.Ordinal);
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
    [InlineData("int64?", "Parameter types are written without '?': a parameter always has a value (got 'int64?').", "write 'int64'; parameter types: ")]
    [InlineData("int32?", "Parameter types are written without '?': a parameter always has a value (got 'int32?').", "write 'int64'; parameter types: ")]
    [InlineData("float32?", "Parameter types are written without '?': a parameter always has a value (got 'float32?').", "write 'float64'; parameter types: ")]
    [InlineData("map<string,int32?>", "Parameter types are written without '?': a parameter always has a value (got 'map<string,int32?>').",
        "write 'map<string,int64>'; parameter types: ")]
    [InlineData("map<string,float32?>", "Parameter types are written without '?': a parameter always has a value (got 'map<string,float32?>').",
        "write 'map<string,float64>'; parameter types: ")]
    [InlineData("list<int64>?", "Parameter types are written without '?': a parameter always has a value (got 'list<int64>?').", "parameter types: ")]
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
    [InlineData("list<float32,8>", TypeSite.Feature, "list<float32,8>", false)]
    [InlineData("list<int64,3>?", TypeSite.Column, "list<int64,3>?", false)]
    [InlineData("float64", TypeSite.Parameter, "float64", false)]
    [InlineData("map<string,float64>", TypeSite.Parameter, "float64", true)]
    public void AValidType_Parses(string written, TypeSite site, string type, bool map)
    {
        Assert.True(ConfigTypes.TryParse(written, site, out var parsed, out var isMap, out _, out _));

        Assert.Equal(ColumnType.Parse(type), parsed);
        Assert.Equal(map, isMap);
    }
}
