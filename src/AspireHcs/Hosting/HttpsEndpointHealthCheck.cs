using System.Net.Security;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AspireHcs.Hosting;

/// <summary>
/// Reports healthy once an HTTPS GET to a resource's endpoint answers with a 2xx/3xx status.
/// With <paramref name="acceptAnyServerCertificate"/> the TLS handshake accepts any certificate
/// — the check then proves the service answers, not the cert's identity. That is the point:
/// Aspire's built-in HTTPS health check validates certificates, which a self-signed appliance
/// can never pass.
/// </summary>
/// <remarks>
/// The endpoint is looked up per check, and the URI built per call: the address can change
/// between boots. One <see cref="HttpClient"/> per check instance — the registration factory
/// creates one instance per registration, so the client's connection pool lives as long as the
/// check does.
/// </remarks>
internal sealed class HttpsEndpointHealthCheck : IHealthCheck
{
    private readonly IResource _resource;
    private readonly string _endpointName;
    private readonly string _path;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _client;

    public HttpsEndpointHealthCheck(
        IResource resource, string endpointName, string path, bool acceptAnyServerCertificate, TimeSpan timeout)
    {
        _resource = resource;
        _endpointName = endpointName;
        _path = path;
        _timeout = timeout;

        SocketsHttpHandler handler = new();
        if (acceptAnyServerCertificate)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }

        // The linked CTS below is the timeout; Timeout.InfiniteTimeSpan keeps HttpClient's own
        // 100 s default from being a second, competing budget.
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        AllocatedEndpoint? allocated = EndpointAllocations.Find(_resource, _endpointName);

        if (allocated is null)
        {
            return HealthCheckResult.Unhealthy(
                $"Endpoint '{_endpointName}' on '{_resource.Name}' is not allocated yet; the guest has no address.");
        }

        Uri uri = new($"https://{allocated.Address}:{allocated.Port}{_path}");

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            using HttpResponseMessage response = await _client.GetAsync(uri, cts.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            return status is >= 200 and < 400
                ? HealthCheckResult.Healthy($"{uri} answered {status}.")
                : HealthCheckResult.Unhealthy($"{uri} answered {status}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"{uri} did not answer within {_timeout}.");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Unhealthy($"{uri} is not answering ({ex.Message}).", ex);
        }
    }
}
