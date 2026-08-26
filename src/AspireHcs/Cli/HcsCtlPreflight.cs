namespace AspireHcs.Cli;

/// <summary>
/// Turns <c>hcsctl info</c> into a go/no-go decision with an actionable reason. Pure policy over a
/// document: no process, no HCS.
/// </summary>
internal static class HcsCtlPreflight
{
    /// <summary>The one wire-contract version AspireHcs understands. Exact string match, not numeric.</summary>
    public const string SupportedContractVersion = "3";

    /// <summary>Services that must be running before any compute system can be created.</summary>
    private static readonly string[] RequiredServices = ["vmcompute", "hvhost"];

    private const string RunningState = "running";

    /// <summary>
    /// Returns why containers cannot run on this host and token, or <see langword="null"/> when
    /// they can.
    /// </summary>
    public static string? DescribeBlocker(HcsCtlInfoDocument info)
    {
        ArgumentNullException.ThrowIfNull(info);

        // The document shape is unknown without a contractVersion, so this gate runs before
        // anything else reads a field.
        if (string.IsNullOrWhiteSpace(info.ContractVersion))
        {
            return "hcsctl did not report a contractVersion, so its document shape is unknown. " +
                "Repin a supported build with ./eng/Get-HcsCtl.ps1 -Force.";
        }

        if (!string.Equals(info.ContractVersion, SupportedContractVersion, StringComparison.Ordinal))
        {
            return $"hcsctl {info.ToolVersion ?? "unknown"} reports contractVersion '{info.ContractVersion}'; " +
                $"AspireHcs supports only '{SupportedContractVersion}'. " +
                "Repin the supported build with ./eng/Get-HcsCtl.ps1 -Force.";
        }

        foreach (string service in RequiredServices)
        {
            if (!info.Services.TryGetValue(service, out string? state))
            {
                return $"hcsctl did not report the state of the '{service}' service, which HCS requires. " +
                    "Confirm the Hyper-V feature is installed.";
            }

            if (!string.Equals(state, RunningState, StringComparison.OrdinalIgnoreCase))
            {
                return $"The '{service}' service is '{state}', not running. " +
                    "HCS cannot create a compute system until it is; enable the Hyper-V feature and start the service.";
            }
        }

        if (!info.HyperVAdministrators)
        {
            return "The current account is not in the Hyper-V Administrators group. " +
                "That membership is what lets AspireHcs run a Hyper-V-isolated container without elevation; " +
                "add the account, then sign out and back in so the token carries the new membership.";
        }

        return null;
    }

    /// <summary>
    /// Returns why <paramref name="imageReference"/> cannot be run yet, or <see langword="null"/>
    /// when it is materialized in the store.
    /// </summary>
    /// <remarks>
    /// Image acquisition is not automated here. <c>image import</c> needs <c>SeBackup</c>/<c>SeRestore</c>
    /// and an enabled <c>BUILTIN\Administrators</c> SID, which a UAC-filtered token does not
    /// have, so an AppHost cannot acquire an image on the developer's behalf. It can only say
    /// what to run.
    /// </remarks>
    public static string? DescribeMissingImage(HcsCtlInfoDocument info, string imageReference)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        bool present = info.Images.Any(
            image => string.Equals(image.Reference, imageReference, StringComparison.OrdinalIgnoreCase));

        if (present)
        {
            return null;
        }

        string store = info.Store?.Root ?? "the hcsctl store";
        string preamble = info.Store?.Exists == true
            ? $"The image '{imageReference}' is not in the hcsctl store ({store})."
            : $"The hcsctl store ({store}) does not exist yet, so '{imageReference}' has not been acquired.";

        return $"{preamble} Acquire it once, from an elevated prompt for the second command:" +
            Environment.NewLine +
            $"  hcsctl image pull   --ref {imageReference}" + Environment.NewLine +
            $"  hcsctl image import --ref {imageReference}   (elevated)" + Environment.NewLine +
            "Only the import needs elevation, and only once per image — running the container afterwards does not.";
    }
}
