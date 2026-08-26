using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace AspireHcs.Hosting;

/// <summary>
/// Dashboard commands that open a session into the guest. The client launches
/// <em>host-side</em>: in local development the AppHost and the browser that shows the
/// dashboard run on the same machine. The guest must serve SSH or RDP; the image provides that.
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
                resource, endpointName, "an SSH session", Environment.UserInteractive,
                CurrentState(context.Services, context.ResourceName),
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
                resource, endpointName, "a Remote Desktop session", Environment.UserInteractive,
                CurrentState(context.Services, context.ResourceName),
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
    /// Shared path for both commands. Every failure is reported to the dashboard.
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
        // UpdateState only governs what the dashboard offers; the command is reachable through
        // Aspire's command APIs regardless, and an allocation outlives the VM that earned it
        // (HcsVmOrchestrator assigns AllocatedEndpoint and never clears it). The predicate is
        // "Running", not "not terminal", so it agrees with Availability: a VM in Starting still
        // carries the previous run's allocation.
        if (!StateAllowsConnect(currentState))
        {
            return Failure(
                $"The virtual machine is {currentState}, so there is nothing to connect to. " +
                "Wait for it to be running.");
        }

        // Session 0 has no desktop to put the client on. Process.Start still succeeds there and
        // leaves an invisible process. The flag is a parameter so a test can reach this branch.
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
            // Usually the client is not installed; the raw Win32 message ("The system cannot
            // find the file specified") does not make that obvious.
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
    /// <c>-l USER</c> keeps the user name out of a delimited token; no escaping is needed.
    /// </para>
    /// <para>
    /// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>. Its quoting survives
    /// the ShellExecuteEx path: spaces, quotes, trailing backslashes and <c>DOMAIN\User Name</c>
    /// arrive at the child unchanged.
    /// </para>
    /// </remarks>
    internal static ProcessStartInfo BuildSshStartInfo(string address, int port, string? userName)
    {
        // UseShellExecute gives the client its own console; it must not share the AppHost's
        // (stdin, log stream). On Windows 11 that console is the user's default terminal.
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
    /// Writes the connection file and returns <c>mstsc.exe FILE</c>. mstsc is named explicitly;
    /// the <c>.rdp</c> file association is not used.
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
    /// Rewritten on every click; the guest's address changes across restarts. The file is not
    /// deleted afterwards: mstsc reads it asynchronously after launch. It holds only an address
    /// and a user name. The directory follows <c>ASPIREHCS_TEMP</c> when set.
    /// </summary>
    internal static string RdpFilePath(HcsVirtualMachineResource resource, string endpointName)
    {
        string directory = Path.Combine(AspireHcsEnvironment.TempDirectory, "connect");
        string path = Path.GetFullPath(Path.Combine(directory, $"{resource.VmId}-{endpointName}.rdp"));

        // The endpoint name becomes part of a file name. This asserts that the file lands
        // inside the intended directory.
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Endpoint name '{endpointName}' would place the .rdp file outside '{directory}'.");
        }

        return path;
    }

    /// <summary>
    /// Whether the state permits connecting. <c>null</c> (state unknown) is permitted: the
    /// resource id <see cref="CurrentState"/> looks up by is not guaranteed to equal the resource
    /// name. <c>Availability</c> has a real snapshot and requires Running.
    /// </summary>
    private static bool StateAllowsConnect(string? currentState)
        => currentState is null || currentState == KnownResourceStates.Running;

    /// <summary>
    /// Best-effort current state for the guard above. Returns null when it cannot be determined.
    /// </summary>
    internal static string? CurrentState(IServiceProvider services, string resourceName)
    {
        try
        {
            ResourceNotificationService notifications =
                services.GetRequiredService<ResourceNotificationService>();
            return notifications.TryGetCurrentState(resourceName, out ResourceEvent? resourceEvent)
                ? resourceEvent.Snapshot.State?.Text
                : null;
        }
        catch (InvalidOperationException)
        {
            // The service is not registered in this host (for example a bare model in a test).
            // Unknown state permits connecting.
            return null;
        }
    }

    private static void ShellExecute(ProcessStartInfo startInfo)
    {
        // Disposing the handle does not disturb the client: it keeps running independently.
        using Process? _ = Process.Start(startInfo);
    }

    /// <summary>
    /// Enabled only when the VM is Running and the endpoint has an address. The guest reaches
    /// Running before its DHCP lease surfaces.
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
