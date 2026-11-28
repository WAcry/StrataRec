namespace StrataRec.Testing;

/// <remarks>
/// The members of a <see cref="CancellationTokenSource"/> are not thread-safe against its disposal: a getter that
/// passed the disposed check just before a concurrent dispose cleared the handle throws
/// <see cref="NullReferenceException"/>.
/// </remarks>
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
        catch (NullReferenceException)
        {
            return false;
        }
    }

    public static bool WaitUntilDisposed(CancellationToken token, TimeSpan patience) => SpinWait.SpinUntil(() => Disposed(token), patience);

    public static void AssertEventuallyDisposed(CancellationToken token) =>
        Assert.True(WaitUntilDisposed(token, Patience), "the token's source was not disposed");
}
