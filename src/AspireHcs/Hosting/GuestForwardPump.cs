using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Cli;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// Starts an <c>hcsctl guest forward</c> for each endpoint named in
/// <see cref="HcsVirtualMachineResource.HvsocketForwardTargets"/>, once the guest agent is
/// confirmed reachable. Mirrors the boot-scoped process ownership of
/// <see cref="SerialConsolePump"/> and the container workload: entered in the boot ledger
/// immediately after starting, torn down with the boot.
/// <para>
/// Never fails the boot. An absent agent, or a forward that fails to start, degrades to
/// nothing — <see cref="ConnectCommands"/> falls back to the leased address it already has,
/// exactly as it did before this existed.
/// </para>
/// </summary>
internal static class GuestForwardPump
{
    public static async Task StartAsync(
        HcsVirtualMachineResource resource,
        HcsCtl hcsctl,
        BootLedger ledger,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (resource.HvsocketForwardTargets.Count == 0)
        {
            return;
        }

        if (!await IsAgentReachableAsync(resource, hcsctl, logger, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug(
                "hcsguest is not reachable in '{Name}'; Connect (SSH) will use the leased address instead of an hvsocket forward.",
                resource.Name);
            return;
        }

        foreach ((string endpointName, int guestPort) in resource.HvsocketForwardTargets)
        {
            await StartOneAsync(resource, hcsctl, ledger, logger, endpointName, guestPort, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsAgentReachableAsync(
        HcsVirtualMachineResource resource, HcsCtl hcsctl, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            HcsCtlGuestInfoDocument info = await hcsctl
                .GuestInfoAsync(resource.VmId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return info.Reachable;
        }
        catch (HcsCtlCommandException)
        {
            // `guest info` reports unreachable as a failure document on a non-zero exit, not a
            // thrown-away success — no agent in the image and "not up yet" look the same here.
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A usage or contract exception here would be a bug in this integration, not an
            // absent agent — still must not fail the boot over the connect button.
            logger.LogDebug(ex, "Could not determine whether hcsguest is reachable in '{Name}'.", resource.Name);
            return false;
        }
    }

    private static async Task StartOneAsync(
        HcsVirtualMachineResource resource,
        HcsCtl hcsctl,
        BootLedger ledger,
        ILogger logger,
        string endpointName,
        int guestPort,
        CancellationToken cancellationToken)
    {
        HcsCtlLongRunningInvocation<HcsCtlGuestForwardDocument> invocation;
        try
        {
            invocation = await hcsctl.GuestForwardAsync(
                resource.VmId,
                guestPort,
                progress: new Progress<string>(line => logger.LogDebug("hcsctl: {Line}", line)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "Starting the hvsocket forward for '{Endpoint}' on '{Name}' failed; Connect (SSH) will use the leased address instead.",
                endpointName, resource.Name);
            return;
        }

        if (invocation.Result.Listen is not { Length: > 0 } listen)
        {
            HcsCtl.KillQuietly(invocation.Process);
            invocation.Process.Dispose();
            logger.LogDebug("hcsctl guest forward reported no listen address for '{Endpoint}' on '{Name}'.",
                endpointName, resource.Name);
            return;
        }

        Process process = invocation.Process;
        ForwardHandle handle = new();

        // A forward that exits on its own mid-session (killed externally, guest agent crash)
        // must not leave the button dialling a dead port. Not raised for the ledger's own kill:
        // TornDown is set there before the process is touched.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (handle.TornDown)
            {
                return;
            }

            resource.ForwardedConnectAddresses.TryRemove(endpointName, out _);
            logger.LogWarning(
                "The hvsocket forward for '{Endpoint}' on '{Name}' exited unexpectedly; Connect (SSH) will use the leased address instead.",
                endpointName, resource.Name);
        };

        // Registered before anything else here can fail — same discipline as the serial console
        // pump and the container workload's ledger entry.
        ledger.Add($"guest forward {endpointName}", () =>
        {
            handle.TornDown = true;
            resource.ForwardedConnectAddresses.TryRemove(endpointName, out _);
            HcsCtl.KillQuietly(process);
            process.Dispose();
        });

        resource.ForwardedConnectAddresses[endpointName] = listen;
        logger.LogInformation("Forwarding '{Endpoint}' to {Listen} over hvsocket.", endpointName, listen);
    }

    private sealed class ForwardHandle
    {
        public volatile bool TornDown;
    }
}
