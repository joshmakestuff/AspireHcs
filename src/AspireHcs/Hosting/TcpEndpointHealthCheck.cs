using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AspireHcs.Hosting;

/// <summary>
/// Reports healthy once a TCP connection to a resource's endpoint is accepted — that is, once
/// something inside the guest is actually listening there.
/// </summary>
/// <remarks>
/// The guest kernel comes up before its services do.
/// <see cref="Hcs.HcsComputeSystem.WaitForGuestReadyAsync"/> attests only that the integration
/// drivers and DHCP answered. This check closes that gap: <c>WaitFor(vm)</c> releases dependents
/// when a workload listens.
/// <para>
/// A refused connection is unhealthy: the guest's network stack is up, but nothing serves yet.
/// </para>
/// <para>
/// The endpoint is looked up per check, not captured:
/// <see cref="EndpointReference.IsAllocated"/> memoizes its first answer, including
/// <see langword="false"/>.
/// </para>
/// </remarks>
/// <para>
/// Serves containers as well as VMs. For a VM, guest-kernel readiness gates Running and this
/// check gates ready. For a container, start already implies the guest is up, so this is the
/// only readiness gate.
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
