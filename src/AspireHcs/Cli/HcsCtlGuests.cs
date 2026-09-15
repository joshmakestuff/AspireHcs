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

    /// <summary>
    /// Runs <c>guest mount</c>: attaches a host SMB share at a guest path, over the guest's own
    /// SMB client. The agent authenticates with a Windows Credential Manager entry named by
    /// <paramref name="credentialTarget"/> — hcsctl reads it itself, so the password never appears
    /// in argv and never reaches AspireHcs.
    /// </summary>
    /// <remarks>
    /// <paramref name="uid"/>/<paramref name="gid"/> map ownership for a Linux cifs mount; 0 (the
    /// default) leaves the cifs default, and both are ignored by a Windows guest.
    /// </remarks>
    public static Task<HcsCtlGuestMountDocument> GuestMountAsync(
        this HcsCtl hcsctl,
        string vmId,
        string unc,
        string path,
        string credentialTarget,
        bool readOnly = false,
        int uid = 0,
        int gid = 0,
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(
            BuildMountArguments(vmId, unc, path, credentialTarget, readOnly, uid, gid, timeout),
            HcsCtlJsonContext.Default.HcsCtlGuestMountDocument, progress, cancellationToken);
    }

    /// <summary>Builds the <c>guest mount</c> arguments.</summary>
    internal static List<string> BuildMountArguments(
        string vmId, string unc, string path, string credentialTarget,
        bool readOnly, int uid, int gid, TimeSpan? timeout)
    {
        List<string> arguments =
        [
            "guest", "mount",
            "--vmid", vmId,
            "--unc", unc,
            "--path", path,
            "--credential", credentialTarget,
        ];

        if (readOnly)
        {
            arguments.Add("--ro");
        }
        if (uid != 0)
        {
            arguments.Add("--uid");
            arguments.Add(uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (gid != 0)
        {
            arguments.Add("--gid");
            arguments.Add(gid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (timeout is { } bound)
        {
            arguments.Add("--timeout");
            arguments.Add(HcsCtlVirtualMachines.FormatDuration(bound));
        }

        return arguments;
    }

    /// <summary>Runs <c>guest unmount</c>: detaches the mount the agent placed at a guest path.</summary>
    public static Task<HcsCtlGuestUnmountDocument> GuestUnmountAsync(
        this HcsCtl hcsctl,
        string vmId,
        string path,
        TimeSpan? timeout = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["guest", "unmount", "--vmid", vmId, "--path", path];
        if (timeout is { } bound)
        {
            arguments.Add("--timeout");
            arguments.Add(HcsCtlVirtualMachines.FormatDuration(bound));
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlGuestUnmountDocument, progress, cancellationToken);
    }
}
