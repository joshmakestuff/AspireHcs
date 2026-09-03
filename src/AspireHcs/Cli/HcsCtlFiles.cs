namespace AspireHcs.Cli;

/// <summary>
/// The unelevated <c>files</c> verbs, as methods — the host side of a VM bind mount. All argv
/// construction for the group is here.
/// </summary>
/// <remarks>
/// <c>files prepare</c> and <c>files remove</c> are deliberately absent: they are elevated,
/// one-time host preparation, run out of band. An AppHost only inspects the preparation and,
/// per run, exposes and unexposes directories under the share root. The group rejects
/// <c>--store</c> (it is keyed off the share root, not an image store), which <see cref="HcsCtl"/>
/// already knows, so nothing here has to.
/// </remarks>
internal static class HcsCtlFiles
{
    /// <summary>
    /// Runs <c>files inspect</c>: whether the host is prepared for VM bind mounts, and which HCN
    /// networks the SMB firewall rule admits. Unelevated, and "not prepared" is a normal answer,
    /// so this exits 0 either way — the preflight reads the document, not the exit code.
    /// </summary>
    public static Task<HcsCtlFilesInspectDocument> InspectFilesAsync(
        this HcsCtl hcsctl,
        string? root = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["files", "inspect"];
        if (!string.IsNullOrEmpty(root))
        {
            arguments.Add("--root");
            arguments.Add(root);
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlFilesInspectDocument, progress, cancellationToken);
    }

    /// <summary>
    /// Runs <c>files expose</c>: makes one host directory reachable to a VM as a junction under the
    /// share root, through the read-write or (with <paramref name="readOnly"/>) read-only share.
    /// </summary>
    public static Task<HcsCtlFilesExposeDocument> ExposeFilesAsync(
        this HcsCtl hcsctl,
        string vmId,
        string name,
        string source,
        bool readOnly = false,
        IReadOnlyDictionary<string, string>? labels = null,
        string? root = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);
        return hcsctl.InvokeAsync(
            BuildExposeArguments(vmId, name, source, readOnly, labels, root),
            HcsCtlJsonContext.Default.HcsCtlFilesExposeDocument, progress, cancellationToken);
    }

    /// <summary>The <c>files expose</c> argv. Pure; pinned by tests.</summary>
    internal static List<string> BuildExposeArguments(
        string vmId, string name, string source, bool readOnly,
        IReadOnlyDictionary<string, string>? labels, string? root)
    {
        List<string> arguments =
        [
            "files", "expose",
            "--vmid", vmId,
            "--name", name,
            "--source", source,
        ];

        if (readOnly)
        {
            arguments.Add("--ro");
        }

        foreach ((string key, string value) in labels ?? new Dictionary<string, string>())
        {
            arguments.Add("--label");
            arguments.Add($"{key}={value}");
        }

        if (!string.IsNullOrEmpty(root))
        {
            arguments.Add("--root");
            arguments.Add(root);
        }

        return arguments;
    }

    /// <summary>
    /// Runs <c>files unexpose</c>: removes one exposure (<paramref name="name"/>) or, when it is
    /// null, all of a VM's. A no-op success when the VM has none, so teardown is idempotent.
    /// </summary>
    public static Task<HcsCtlFilesUnexposeDocument> UnexposeFilesAsync(
        this HcsCtl hcsctl,
        string vmId,
        string? name = null,
        string? root = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["files", "unexpose", "--vmid", vmId];
        if (!string.IsNullOrEmpty(name))
        {
            arguments.Add("--name");
            arguments.Add(name);
        }
        if (!string.IsNullOrEmpty(root))
        {
            arguments.Add("--root");
            arguments.Add(root);
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlFilesUnexposeDocument, progress, cancellationToken);
    }

    /// <summary>Runs <c>files ls</c>: every recorded exposure under the share root, for scavenging.</summary>
    public static Task<HcsCtlFilesListDocument> ListFilesAsync(
        this HcsCtl hcsctl,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hcsctl);

        List<string> arguments = ["files", "ls"];
        if (!string.IsNullOrEmpty(root))
        {
            arguments.Add("--root");
            arguments.Add(root);
        }

        return hcsctl.InvokeAsync(arguments, HcsCtlJsonContext.Default.HcsCtlFilesListDocument, cancellationToken: cancellationToken);
    }
}
