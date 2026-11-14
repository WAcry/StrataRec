using StackExchange.Redis;
using StrataRec.Redis;

namespace StrataRec.UnitTests.Redis;

/// <summary>
/// <c>redis@1</c>'s lifetime without a Redis: the connect is held by the test, and what it connects to (port 1 on the
/// loopback) refuses, so the connection it makes is one that keeps reconnecting in the background until disposed.
/// Against a real Redis: <c>RedisCacheTests</c> in the integration tests.
/// </summary>
public class RedisCacheStoreTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Review 19: a cancelled load stops waiting for the connect, which goes on; the connection it makes later is the
    /// store's, and its disposal waits for it and disposes it.
    /// </summary>
    [Fact]
    public async Task AConnectionMadeAfterTheLoadWasCancelled_IsDisposedWithTheStore()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var made = new TaskCompletionSource<ConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RedisCacheStore(Settings("127.0.0.1:1"), async options =>
        {
            await release.Task;
            options.ConnectTimeout = 100;
            var connection = await ConnectionMultiplexer.ConnectAsync(options);
            made.SetResult(connection);
            return connection;
        });
        using var cancel = new CancellationTokenSource();
        var loading = store.LoadAsync(cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Wait));

        var disposing = store.DisposeAsync().AsTask();
        release.SetResult(); // the connect ends only now, after the disposal began
        var connection = await made.Task.WaitAsync(Wait);
        await disposing.WaitAsync(Wait);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.GetDatabase().PingAsync());
    }

    [Fact]
    public async Task AStoreNeverLoaded_IsDisposedAtOnce()
    {
        var connects = 0;
        var store = new RedisCacheStore(Settings("127.0.0.1:1"), _ =>
        {
            connects++;
            return Task.FromException<ConnectionMultiplexer>(new InvalidOperationException("never called"));
        });

        await store.DisposeAsync();

        Assert.Equal(0, connects);
    }

    private static RedisStoreSettings Settings(string endpoint) => new(endpoint, "stratarec-test:", null, null, Tls: false, Database: 0);
}
