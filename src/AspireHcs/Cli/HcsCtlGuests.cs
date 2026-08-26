namespace AspireHcs.Cli;

/// <summary>
/// The <c>guest</c> verbs, as methods — the hvsocket path into a VM's guest. Same reason as
/// <see cref="HcsCtlContainers"/>: an option spelled wrong is exit 64, indistinguishable from a
/// bad value in a resource's configuration.
///
/// The transport is a Hyper-V socket, so nothing here needs a NIC, a DHCP lease, or elevation —
/// but everything here needs the <c>hcsguest</c> agent in the image. The group rejects
/// <c>--store</c> (a guest is addressed by its VM id, not through a store), which
/// <see cref="HcsCtl"/> already knows, so nothing here has to.
/// </summary>
internal static class HcsCtlGuests
{
    /// <summary>
    /// Runs one command inside the guest through its shell — <c>/bin/sh -c</c> on Linux,
    /// <c>cmd /c</c> on Windows — and waits for it.
    /// </summary>
    /// <remarks>
    /// The dial has its own 35 s budget inside hcsctl, separate from <paramref name="timeout"/>:
    /// reaching the guest and running the command are different waits, and an image without the
    /// agent fails the dial, not the command. Environment goes as <c>--env</c>, added to the
    /// guest's own environment — which is how a value crosses into the guest without being
    /// re-quoted through its shell.
    /// </remarks>
    public static Task<HcsCtlGuestExecDocument> GuestExecAsync(
        this HcsCtl hcsctl,
        string vmId,
        string commandLine,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["guest", "exec", "--vmid", vmId, "--cmd", commandLine];

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            arguments.Add("--env");
            arguments.Add($"{name}={value}");
        }

        if (timeout is { } bound)
        {
            arguments.Add("--timeout");
            arguments.Add(HcsCtlVirtualMachines.FormatDuration(bound));
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlGuestExecDocument, progress, cancellationToken);
    }
}
