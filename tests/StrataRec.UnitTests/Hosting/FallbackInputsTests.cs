using StrataRec.Core.Types;
using StrataRec.Core.Values;
using StrataRec.Hosting;
using StrataRec.Pipeline.Planning;

namespace StrataRec.UnitTests.Hosting;

/// <summary>
/// A fallback pipeline stands in for its primary with the inputs it declares (<see cref="RecEngine.FallbackInputs"/>):
/// those it does not declare are not its caller's mistake, and the request was valid for the pipeline it asked for.
/// </summary>
public sealed class FallbackInputsTests
{
    private static readonly RequestSchema Backup = new("backup", new Dictionary<string, ColumnType>
    {
        ["user_id"] = ColumnType.Parse("int64"),
        ["surface"] = ColumnType.Parse("string?"),
    });

    [Fact]
    public void InputsTheFallbackDeclares_AreTheSameInstance()
    {
        var inputs = new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("surface", ScalarValue.FromString("home"));

        Assert.Same(inputs, RecEngine.FallbackInputs(inputs, Backup)); // nothing allocated for the common case
        var none = new RequestInputs();
        Assert.Same(none, RecEngine.FallbackInputs(none, Backup));
    }

    [Fact]
    public void InputsTheFallbackDoesNotDeclare_AreLeftOut_AndTheCallersAreUnchanged()
    {
        var inputs = new RequestInputs().Set("user_id", ScalarValue.FromInt64(1)).Set("region", ScalarValue.FromString("us")).Set("session_id", ScalarValue.FromString("s"));

        var projected = RecEngine.FallbackInputs(inputs, Backup);

        Assert.Equal(new[] { "user_id" }, projected.Select(p => p.Key));
        Assert.Equal(1L, projected["user_id"].AsInt64());
        Assert.Equal(3, inputs.Count);
        Assert.Null(Backup.Check(projected));
    }
}
