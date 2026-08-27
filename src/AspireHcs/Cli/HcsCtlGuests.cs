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

    /// <summary>
    /// Runs <c>guest info</c>: what the guest agent says about itself, over hvsocket. The
    /// forward pump's agent-presence check — a VM whose image has no <c>hcsguest</c>, or one not
    /// yet up, answers <see cref="HcsCtlGuestInfoDocument.Reachable"/> false here rather than
    /// leaving a forward half-started.
    /// </summary>
    public static Task<HcsCtlGuestInfoDocument> GuestInfoAsync(
        this HcsCtl hcsctl,
        string vmId,
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["guest", "info", "--vmid", vmId];

        if (timeout is { } bound)
        {
            arguments.Add("--timeout");
            arguments.Add(HcsCtlVirtualMachines.FormatDuration(bound));
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlGuestInfoDocument, progress, cancellationToken);
    }

    /// <summary>
    /// Starts <c>guest forward --vmid &lt;id&gt; --port &lt;guestPort&gt; --listen 127.0.0.1:0</c>:
    /// a Hyper-V-socket relay of one guest TCP port to an OS-assigned host loopback port. Returns
    /// once the listener is up and the bound address is known — see
    /// <see cref="HcsCtl.StartLongRunningAsync{TResult}"/> — not once the relay stops; the caller
    /// owns the process and kills it when the forward is no longer wanted.
    /// </summary>
    public static Task<HcsCtlLongRunningInvocation<HcsCtlGuestForwardDocument>> GuestForwardAsync(
        this HcsCtl hcsctl,
        string vmId,
        int guestPort,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(guestPort, 0);

        List<string> arguments =
        [
            "guest", "forward",
            "--vmid", vmId,
            "--port", guestPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--listen", "127.0.0.1:0",
        ];

        return hcsctl.StartLongRunningAsync(arguments, HcsCtlJsonContext.Default.HcsCtlGuestForwardDocument, progress, cancellationToken);
    }
}
