using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace AspireHcs.Tests;

/// <summary>
/// Drives <see cref="HttpsEndpointHealthCheck"/> against an in-process TLS listener with a
/// self-signed certificate — the appliance shape: the service answers, the certificate can
/// never validate.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class HttpsEndpointHealthCheckTests
{
    [Fact]
    public async Task Check_is_unhealthy_until_the_endpoint_is_allocated()
    {
        HcsVirtualMachineResource resource = new("vm");
        resource.Annotations.Add(Endpoint("https", 443));

        HealthCheckResult result = await Check(resource, acceptAnyServerCertificate: true)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not allocated", result.Description);
    }

    [Fact]
    public async Task A_2xx_behind_a_self_signed_cert_is_healthy_only_when_the_cert_is_accepted()
    {
        await using SelfSignedServer server = SelfSignedServer.Start("HTTP/1.1 200 OK");
        HcsVirtualMachineResource resource = Allocated(server.Port);

        // acceptAnyServerCertificate: the point of the option — the same endpoint, same
        // response, flips on trust alone.
        HealthCheckResult tolerant = await Check(resource, acceptAnyServerCertificate: true)
            .CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, tolerant.Status);

        HealthCheckResult strict = await Check(resource, acceptAnyServerCertificate: false)
            .CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, strict.Status);
    }

    [Fact]
    public async Task A_5xx_is_unhealthy_even_over_an_accepted_cert()
    {
        await using SelfSignedServer server = SelfSignedServer.Start("HTTP/1.1 500 Internal Server Error");
        HcsVirtualMachineResource resource = Allocated(server.Port);

        HealthCheckResult result = await Check(resource, acceptAnyServerCertificate: true)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("500", result.Description);
    }

    private static HcsVirtualMachineResource Allocated(int port)
    {
        HcsVirtualMachineResource resource = new("vm");
        EndpointAnnotation endpoint = Endpoint("https", port);
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", port);
        resource.Annotations.Add(endpoint);
        return resource;
    }

    private static IHealthCheck Check(HcsVirtualMachineResource resource, bool acceptAnyServerCertificate)
        => new HttpsEndpointHealthCheck(resource, "https", "/", acceptAnyServerCertificate, TimeSpan.FromSeconds(10));

    private static EndpointAnnotation Endpoint(string name, int targetPort)
        => new(ProtocolType.Tcp, name: name, targetPort: targetPort, isProxied: false);

    /// <summary>
    /// A loopback TLS listener answering every request with one canned status line. The
    /// certificate is generated per server and trusted by nobody, exactly like an appliance's.
    /// </summary>
    private sealed class SelfSignedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _accepting;

        public int Port { get; }

        private SelfSignedServer(string statusLine)
        {
            using RSA key = RSA.Create(2048);
            CertificateRequest request = new("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // SChannel refuses an ephemeral private key for server auth; a PFX round-trip
            // persists the key the way it expects.
            _certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), password: null);

            _listener = new TcpListener(IPAddress.Loopback, port: 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accepting = AcceptLoopAsync(statusLine, _cts.Token);
        }

        public static SelfSignedServer Start(string statusLine) => new(statusLine);

        private async Task AcceptLoopAsync(string statusLine, CancellationToken cancellationToken)
        {
            byte[] response = Encoding.ASCII.GetBytes(
                $"{statusLine}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // Sequential is enough: each check makes one request. A handshake the client
                // aborts (the strict-validation case) throws here and must not kill the loop.
                try
                {
                    using TcpClient connection = client;
                    await using SslStream tls = new(connection.GetStream());
                    await tls.AuthenticateAsServerAsync(_certificate).ConfigureAwait(false);

                    byte[] buffer = new byte[4096];
                    int read;
                    StringBuilder requestText = new();
                    while ((read = await tls.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        requestText.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (requestText.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }

                    await tls.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    await tls.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The client rejected the cert mid-handshake; the next check opens a new
                    // connection.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _accepting.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown; the loop's own error handling has already spoken.
            }
            _certificate.Dispose();
            _cts.Dispose();
        }
    }
}
