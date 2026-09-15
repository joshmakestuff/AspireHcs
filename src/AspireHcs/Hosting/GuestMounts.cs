using Aspire.Hosting.ApplicationModel;
using AspireHcs.Cli;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// Applies a VM's <see cref="HcsVirtualMachineResource.Mounts"/> once the guest is up: for each,
/// the host exposes the source directory under the share (<c>files expose</c>) and the guest
/// agent mounts it (<c>guest mount</c>). Every step is registered in the boot ledger, so a
/// failure part-way fails the boot and the ledger unwinds what was done.
/// </summary>
/// <remarks>
/// Teardown order is unmount-then-unexpose: one <c>files unexpose --vmid</c> is registered before
/// the first expose (so it drains last, after the guest has released every SMB session), and each
/// <c>guest unmount</c> is registered after its mount (so it drains first). Host junctions and
/// ACEs outlive the compute system — removing the VM does not remove them — so this teardown, and
/// the crash scavenger behind it, is what keeps them from leaking.
/// </remarks>
internal static class GuestMounts
{
    /// <summary>
    /// The Windows Credential Manager target <c>hcsctl files prepare</c> stores the share user's
    /// password under, and <c>hcsctl guest mount --credential</c> reads it back from. A wire
    /// contract with hcsctl (<c>internal/files</c>'s <c>CredentialTarget</c>); the password itself
    /// never reaches AspireHcs.
    /// </summary>
    private const string ShareCredentialTarget = "hcsctl-files";

    /// <summary>Budget for one guest mount. The dial has its own budget inside hcsctl.</summary>
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Bounds each teardown call, which runs on a fresh token, not the boot's.</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(30);

    public static async Task MountAllAsync(
        HcsVirtualMachineResource resource,
        HcsCtl hcsctl,
        BootLedger ledger,
        string gateway,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(hcsctl);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateway);

        if (resource.Mounts.Count == 0)
        {
            return;
        }

        // One unexpose removes every exposure this boot creates for the VM, so it is registered
        // once, before the first expose: a failure at any point still removes all host junctions.
        // Runs on a fresh token (like RemoveVm) so a shutdown-cancelled boot still tears down.
        ledger.Add($"file exposures for {resource.VmId}", () =>
        {
            using CancellationTokenSource cts = new(TeardownTimeout);
            try
            {
                hcsctl.UnexposeFilesAsync(resource.VmId, cancellationToken: cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unexposing files for VM {VmId} failed; the next run will scavenge them.", resource.VmId);
            }
        });

        var progress = new Progress<string>(line => logger.LogDebug("hcsctl: {Line}", line));

        for (int i = 0; i < resource.Mounts.Count; i++)
        {
            HcsVmMount mount = resource.Mounts[i];
            string name = HcsVirtualMachineResource.ExposureName(i);

            HcsCtlFilesExposeDocument exposed = await hcsctl.ExposeFilesAsync(
                resource.VmId,
                name,
                mount.Source,
                mount.IsReadOnly,
                labels: new Dictionary<string, string> { [HcsVmOrchestrator.OwnerPidLabel] = HcsVmOrchestrator.OwnerPidValue },
                progress: progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            string share = exposed.Share
                ?? throw new InvalidOperationException($"hcsctl exposed '{name}' for '{resource.Name}' but reported no share.");
            string relativePath = exposed.RelativePath
                ?? throw new InvalidOperationException($"hcsctl exposed '{name}' for '{resource.Name}' but reported no relative path.");

            string unc = BuildUnc(gateway, share, relativePath);

            await hcsctl.GuestMountAsync(
                resource.VmId,
                unc,
                mount.Target,
                ShareCredentialTarget,
                readOnly: mount.IsReadOnly,
                timeout: MountTimeout,
                progress: progress,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Captured by value so each release unmounts its own path.
            string target = mount.Target;
            ledger.Add($"guest mount {target}", () =>
            {
                using CancellationTokenSource cts = new(TeardownTimeout);
                try
                {
                    hcsctl.GuestUnmountAsync(resource.VmId, target, cancellationToken: cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Unmounting {Path} in VM {VmId} failed.", target, resource.VmId);
                }
            });

            logger.LogInformation("Mounted {Source} at {Target} ({Mode}) in '{Name}'.",
                mount.Source, mount.Target, mount.IsReadOnly ? "read-only" : "read-write", resource.Name);
        }
    }

    /// <summary>
    /// The UNC a guest mounts: <c>\\&lt;gateway&gt;\&lt;share&gt;\&lt;relativePath&gt;</c>. The
    /// relative path already carries the <c>&lt;vmId&gt;\&lt;name&gt;</c> tail hcsctl reported.
    /// </summary>
    public static string BuildUnc(string gateway, string share, string relativePath)
        => $@"\\{gateway}\{share}\{relativePath}";
}
