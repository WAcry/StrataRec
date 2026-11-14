namespace StrataRec.Testing;

/// <summary>
/// Whether a token's source is disposed, asked through the one public member that says so,
/// <see cref="CancellationToken.WaitHandle"/>, which throws <see cref="ObjectDisposedException"/> once it is.
/// </summary>
/// <remarks>
/// A framework token source completes its disposal once the callbacks its cancellation queued have run
/// (<see cref="StrataRec.Core.Threading.ObservedCancellationSource"/>), which may be a moment after the call that holds the
/// token ended, on another thread: a test waits for it, within a bound. The members of a
/// <see cref="CancellationTokenSource"/> are not thread-safe against its disposal: a getter that passed the disposed check
/// just before a concurrent dispose cleared the handle throws <see cref="NullReferenceException"/> (seen on CI). That means
/// "not yet": the next look sees the disposal. The one probe lives here so that every test reads it the same way.
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
            return false; // the disposal is running on another thread right now
        }
    }

    /// <summary>Waits until the token's source is disposed; false when it is not within <paramref name="patience"/>.</summary>
    public static bool WaitUntilDisposed(CancellationToken token, TimeSpan patience) => SpinWait.SpinUntil(() => Disposed(token), patience);

    public static void AssertEventuallyDisposed(CancellationToken token) =>
        Assert.True(WaitUntilDisposed(token, Patience), "the token's source was not disposed");
}
