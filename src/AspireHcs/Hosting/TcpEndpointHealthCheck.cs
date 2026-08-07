using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AspireHcs.Hosting;

/// <summary>
/// Reports healthy once a TCP connection to a resource's endpoint is accepted — that is, once
/// something inside the guest is actually listening there.
/// </summary>
/// <remarks>
/// The guest's kernel comes up well before its services do: on the reference image the
/// integration drivers answer at ~9 s and DHCP at ~14 s, which is all
/// <see cref="Hcs.HcsComputeSystem.WaitForGuestReadyAsync"/> can attest to. This is the
/// signal that closes that gap, so <c>WaitFor(vm)</c> releases dependents against a
/// workload rather than a login prompt.
/// <para>
/// A refused connection is unhealthy, not healthy. A refusal proves the guest's network
/// stack is up — which is why the round-trip test accepts it as evidence of reachability —
/// but it is precisely the case where nothing is serving yet, so treating it as ready
/// would reintroduce the gap this closes.
/// </para>
/// <para>
/// The endpoint is looked up per check rather than captured, because
/// <see cref="EndpointReference.IsAllocated"/> memoizes its first answer including
/// <see langword="false"/>; a reference built at model-build time would latch unallocated
/// forever.
/// </para>
/// </remarks>
/// <para>
/// Serves containers as well as VMs (#41), and the difference in what it <em>means</em> matters.
/// For a VM, guest-kernel readiness gates Running and this gates ready. A container has no
/// separate kernel-readiness signal — start already implies the guest is up — so this is the only
/// readiness gate there is.
/// </para>
internal sealed class TcpEndpointHealthCheck(
    IResource resource,
    string endpointName,
    TimeSpan timeout) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        AllocatedEndpoint? allocated = EndpointAllocations.Find(resource, endpointName);

        if (allocated is null)
        {
            return HealthCheckResult.Unhealthy(
                $"Endpoint '{endpointName}' on '{resource.Name}' is not allocated yet; the guest has no address.");
        }

        // A guest that drops the SYN outright would otherwise hang here for the OS retry
        // budget (~21 s on Windows), stalling the health monitor's loop well past its own
        // polling interval.
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using TcpClient client = new();
        try
        {
            await client.ConnectAsync(allocated.Address, allocated.Port, cts.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                $"{allocated.Address}:{allocated.Port} accepted a connection.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"{allocated.Address}:{allocated.Port} did not answer within {timeout}.");
        }
        catch (SocketException ex)
        {
            return HealthCheckResult.Unhealthy(
                $"{allocated.Address}:{allocated.Port} is not accepting connections ({ex.SocketErrorCode}).", ex);
        }
    }
}
