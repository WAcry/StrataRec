using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StackExchange.Redis;
using StrataRec.Core.Bindings;
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
            options.AbortOnConnectFail = false; // a connection, though nothing listens: the load asked for one that must succeed
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

    /// <summary>
    /// Review 35 #6: a server certificate this host does not trust fails the load at <c>/tls</c>, not as a missing
    /// password. The client reports a failed handshake as an authentication failure too, so the TLS failure is looked for
    /// first, wherever the failure is read; a command's failure on the connection used to be read as refused credentials.
    /// </summary>
    [Fact]
    public async Task AServerCertificateTheHostDoesNotTrust_FailsTheLoadAtTls()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=not-the-endpoint", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var serving = ServeTlsAsync(listener, certificate, stop.Token);
        try
        {
            await using var store = new RedisCacheStore(new RedisStoreSettings(listener.LocalEndpoint.ToString()!, "stratarec-test:", null, null, Tls: true, Database: 0));

            var error = await Assert.ThrowsAsync<BindingException>(() => store.LoadAsync(CancellationToken.None).AsTask().WaitAsync(Wait));

            Assert.Equal("/tls", error.Pointer);
            Assert.Contains("TLS handshake", error.Reason);
            Assert.Contains("certificate", error.Hint);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await serving.WaitAsync(Wait);
        }
    }

    /// <summary>Answers each connection with a TLS handshake that presents <paramref name="certificate"/>, until stopped.</summary>
    private static async Task ServeTlsAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken stop)
    {
        var handshakes = new List<Task>();
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception)
            {
                break;
            }

            handshakes.Add(HandshakeAsync(client, certificate));
        }

        await Task.WhenAll(handshakes);

        static async Task HandshakeAsync(TcpClient client, X509Certificate2 certificate)
        {
            using (client)
            {
                await using var tls = new SslStream(client.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(certificate).WaitAsync(Wait);
                }
                catch (Exception)
                {
                    // The client refuses the certificate.
                }
            }
        }
    }

    private static RedisStoreSettings Settings(string endpoint) => new(endpoint, "stratarec-test:", null, null, Tls: false, Database: 0);
}
