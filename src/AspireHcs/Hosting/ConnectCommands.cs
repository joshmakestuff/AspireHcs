using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace AspireHcs.Hosting;

/// <summary>
/// Dashboard commands that open a session into the guest. These launch the client
/// <em>host-side</em>, which works because in local development the AppHost runs on the same
/// machine as the browser showing the dashboard — the same assumption the rest of Aspire's
/// run mode makes. There is deliberately nothing guest-side here: the guest only has to serve
/// SSH or RDP, which is the image's business, not the integration's.
/// </summary>
internal static class ConnectCommands
{
    internal const string SshCommandName = "connect-ssh";
    internal const string RdpCommandName = "connect-rdp";

    internal static void RegisterSsh(
        IResourceBuilder<HcsVirtualMachineResource> builder, string endpointName, string? userName)
    {
        HcsVirtualMachineResource resource = builder.Resource;

        builder.WithCommand(
            SshCommandName,
            "Connect (SSH)",
            // Interactivity and state are read per click, not captured at model-build time.
            context => Task.FromResult(Execute(
                resource, endpointName, "an SSH session", Environment.UserInteractive, CurrentState(context),
                allocated => BuildSshStartInfo(allocated.Address, allocated.Port, userName),
                ShellExecute)),
            new CommandOptions
            {
                Description = $"Open an SSH session to the guest on the '{endpointName}' endpoint.",
                IconName = "WindowConsole",
                IconVariant = IconVariant.Regular,
                IsHighlighted = true,
                UpdateState = context => Availability(resource, endpointName, context),
            });
    }

    internal static void RegisterRdp(
        IResourceBuilder<HcsVirtualMachineResource> builder, string endpointName, string? userName)
    {
        HcsVirtualMachineResource resource = builder.Resource;

        builder.WithCommand(
            RdpCommandName,
            "Connect (RDP)",
            context => Task.FromResult(Execute(
                resource, endpointName, "a Remote Desktop session", Environment.UserInteractive, CurrentState(context),
                allocated => BuildRdpStartInfo(resource, endpointName, allocated.Address, allocated.Port, userName),
                ShellExecute)),
            new CommandOptions
            {
                Description = $"Open a Remote Desktop session to the guest on the '{endpointName}' endpoint.",
                IconName = "Desktop",
                IconVariant = IconVariant.Regular,
                IsHighlighted = true,
                UpdateState = context => Availability(resource, endpointName, context),
            });
    }

    /// <summary>
    /// The one path both commands take, so the preconditions cannot be remembered in one place
    /// and forgotten in the other. Every failure is reported to the dashboard: a connect button
    /// that appears to do nothing is the worst outcome available here.
    /// </summary>
    internal static ExecuteCommandResult Execute(
        HcsVirtualMachineResource resource,
        string endpointName,
        string sessionDescription,
        bool userInteractive,
        string? currentState,
        Func<AllocatedEndpoint, ProcessStartInfo> build,
        Action<ProcessStartInfo> launch)
    {
        // UpdateState only governs what the dashboard *offers*; the command itself is reachable
        // through Aspire's command APIs regardless, and an allocation outlives the VM that
        // earned it (HcsVmOrchestrator assigns AllocatedEndpoint and never clears it). Without
        // this, invoking the command on a stopped VM would launch a client at last run's
        // address. Unknown state is allowed through rather than refused: the resource id this
        // is looked up by is not guaranteed to equal the resource name, and a lookup miss must
        // not turn into a feature that silently stops working.
        if (currentState is not null && KnownResourceStates.TerminalStates.Contains(currentState))
        {
            return Failure(
                $"The virtual machine is {currentState}, so there is nothing to connect to. " +
                "Start it first.");
        }

        // Session 0 has no desktop to put the client on. Process.Start would still "succeed"
        // there, leaving an invisible process and a dashboard reporting success — so this is
        // checked rather than discovered. Passed in rather than read here so the branch is
        // reachable from a test: a guard that can only ever see `true` is not a guard.
        if (!userInteractive)
        {
            return Failure(
                "The AppHost is not running in an interactive session, so a client window has " +
                "nowhere to appear. Connect commands only work when the AppHost and the browser " +
                "share a desktop.");
        }

        if (EndpointAllocations.Find(resource, endpointName) is not { } allocated)
        {
            return Failure(
                $"Endpoint '{endpointName}' has no address yet. The guest gets one when its DHCP " +
                "lease is discovered, shortly after it boots.");
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = build(allocated);
        }
        catch (Exception ex)
        {
            return Failure($"Could not prepare {sessionDescription}: {ex.Message}");
        }

        try
        {
            launch(startInfo);
        }
        catch (Exception ex)
        {
            // Overwhelmingly the client is simply not installed, which the raw Win32 message
            // ("The system cannot find the file specified") does not make obvious.
            return Failure(
                $"Could not start '{startInfo.FileName}': {ex.Message} " +
                "Check that the client is installed and on PATH.");
        }

        return new ExecuteCommandResult
        {
            Success = true,
            Message = string.Create(CultureInfo.InvariantCulture,
                $"Opened {sessionDescription} to {allocated.Address}:{allocated.Port}."),
        };
    }

    /// <summary>
    /// <c>ssh.exe -p PORT [-l USER] ADDRESS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-l USER</c> rather than <c>USER@ADDRESS</c>: the user name then never meets a
    /// delimiter it could itself contain, so there is no escaping decision to get wrong.
    /// </para>
    /// <para>
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, whose quoting was
    /// verified to survive the ShellExecuteEx path intact — spaces, quotes, trailing
    /// backslashes and <c>DOMAIN\User Name</c> all arrive at the child unchanged. Hand-rolling
    /// an argv escaper here would be reimplementing something the framework already does.
    /// </para>
    /// </remarks>
    internal static ProcessStartInfo BuildSshStartInfo(string address, int port, string? userName)
    {
        // UseShellExecute gives the client its own console rather than making it share the
        // AppHost's, where it would compete for stdin and scribble over the log stream. On
        // Windows 11 that console is whatever the user set as their default terminal.
        ProcessStartInfo startInfo = new("ssh.exe") { UseShellExecute = true };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(userName))
        {
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(userName);
        }

        startInfo.ArgumentList.Add(address);
        return startInfo;
    }

    /// <summary>
    /// Writes the connection file and returns <c>mstsc.exe FILE</c>. mstsc is named explicitly
    /// rather than shell-opening the <c>.rdp</c>, so the command does what it says regardless
    /// of which application currently owns that file association.
    /// </summary>
    internal static ProcessStartInfo BuildRdpStartInfo(
        HcsVirtualMachineResource resource, string endpointName, string address, int port, string? userName)
    {
        string path = RdpFilePath(resource, endpointName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, RdpFile.Build(address, port, userName), RdpFile.FileEncoding);

        ProcessStartInfo startInfo = new("mstsc.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    /// <summary>
    /// Rewritten on every click, since the guest's address changes across restarts. It is left
    /// behind afterwards: mstsc reads it asynchronously after launch, so deleting it here would
    /// be a race, and it holds nothing secret — an address and a user name.
    /// </summary>
    internal static string RdpFilePath(HcsVirtualMachineResource resource, string endpointName)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AspireHcs", "connect");
        string path = Path.GetFullPath(Path.Combine(directory, $"{resource.VmId}-{endpointName}.rdp"));

        // The endpoint name is interpolated into a file name, so it crosses into path syntax.
        // Rather than reimplementing Aspire's own name rules (which it owns, and enforces
        // through the [EndpointName] analyzer), this asserts the property that actually
        // matters to this method: the file lands inside the directory it was meant to.
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Endpoint name '{endpointName}' would place the .rdp file outside '{directory}'.");
        }

        return path;
    }

    /// <summary>
    /// Best-effort current state for the terminal-state guard. Returns null when it cannot be
    /// determined — see <see cref="Execute"/> for why that is allowed through rather than
    /// refused.
    /// </summary>
    private static string? CurrentState(ExecuteCommandContext context)
    {
        try
        {
            ResourceNotificationService notifications =
                context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
            return notifications.TryGetCurrentState(context.ResourceName, out ResourceEvent? resourceEvent)
                ? resourceEvent.Snapshot.State?.Text
                : null;
        }
        catch (InvalidOperationException)
        {
            // The service is not registered in this host (a bare model in a test, say). Not
            // knowing the state is not a reason to refuse to connect.
            return null;
        }
    }

    private static void ShellExecute(ProcessStartInfo startInfo)
    {
        // Disposing the handle does not disturb the client: it keeps running independently.
        using Process? _ = Process.Start(startInfo);
    }

    /// <summary>
    /// Offered only when there is something to connect to. Running alone is not enough — the
    /// guest reaches Running before its DHCP lease surfaces, and a connect button that is live
    /// during that window produces a failed connection rather than a wait.
    /// </summary>
    private static ResourceCommandState Availability(
        HcsVirtualMachineResource resource, string endpointName, UpdateCommandStateContext context)
    {
        if (context.ResourceSnapshot.State?.Text != KnownResourceStates.Running)
        {
            return ResourceCommandState.Disabled;
        }

        return EndpointAllocations.Find(resource, endpointName) is null
            ? ResourceCommandState.Disabled
            : ResourceCommandState.Enabled;
    }

    private static ExecuteCommandResult Failure(string message)
        => new() { Success = false, Message = message };
}
