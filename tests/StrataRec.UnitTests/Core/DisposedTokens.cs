namespace StrataRec.UnitTests.Core;

/// <summary>
/// A framework token source completes its disposal once the callbacks its cancellation queued have run
/// (<see cref="StrataRec.Core.Threading.ObservedCancellationSource"/>), which may be a moment after the call that holds
/// the token ended: a test checks the disposal by waiting for it, within a bound.
/// </summary>
internal static class DisposedTokens
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static bool Disposed(CancellationToken token)
    {
        try
        {
            _ = token.WaitHandle;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public static void AssertEventuallyDisposed(CancellationToken token) =>
        Assert.True(SpinWait.SpinUntil(() => Disposed(token), Patience), "the token's source was not disposed");
}
