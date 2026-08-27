using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Cli;

namespace AspireHcs.Hosting;

/// <summary>
/// The "Connect (Shell)" dashboard command for <see cref="HcsContainerResource"/>: opens an
/// interactive console on the host attached to a new guest process via
/// <c>hcsctl container exec --interactive --tty</c>. Unlike <see cref="ConnectCommands"/>'s SSH
/// and RDP commands, no endpoint or address is required — the transport is HcsCreateProcess
/// stdio, not a network dial. No host-side tracking of the launched hcsctl process is done: if
/// the container is stopped while a shell is attached, hcsctl's own exec loop hits a broken pipe
/// and exits with an error printed to the same console the user is looking at.
/// </summary>
internal static class ContainerConnectCommands
{
    internal const string ShellCommandName = "connect-shell";

    internal static void RegisterShell(IResourceBuilder<HcsContainerResource> builder, string shell)
    {
        HcsContainerResource resource = builder.Resource;

        builder.WithCommand(
            ShellCommandName,
            "Connect (Shell)",
            // Interactivity and state are read per click, not captured at model-build time.
            context => Task.FromResult(Execute(
                resource, shell, Environment.UserInteractive,
                ConnectAvailability.CurrentState(context.Services, context.ResourceName),
                BuildShellStartInfo,
                ConnectAvailability.ShellExecute)),
            new CommandOptions
            {
                Description = $"Open an interactive '{shell}' session inside the container.",
                IconName = "WindowConsole",
                IconVariant = IconVariant.Regular,
                IsHighlighted = true,
                UpdateState = context => Availability(context),
            });
    }

    /// <summary>
    /// Shared path for the command. Every failure is reported to the dashboard, never thrown.
    /// </summary>
    internal static ExecuteCommandResult Execute(
        HcsContainerResource resource,
        string shell,
        bool userInteractive,
        string? currentState,
        Func<HcsContainerResource, string, ProcessStartInfo> build,
        Action<ProcessStartInfo> launch)
    {
        if (!ConnectAvailability.StateAllowsConnect(currentState))
        {
            return ConnectAvailability.Failure(
                $"The container is {currentState}, so there is nothing to attach a shell to. " +
                "Wait for it to be running.");
        }

        // Session 0 has no desktop to put the console on. Process.Start still succeeds there and
        // leaves an invisible process. The flag is a parameter so a test can reach this branch.
        if (!userInteractive)
        {
            return ConnectAvailability.Failure(
                "The AppHost is not running in an interactive session, so a console window has " +
                "nowhere to appear. Connect commands only work when the AppHost and the browser " +
                "share a desktop.");
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = build(resource, shell);
        }
        catch (Exception ex)
        {
            return ConnectAvailability.Failure($"Could not prepare the shell session: {ex.Message}");
        }

        try
        {
            launch(startInfo);
        }
        catch (Exception ex)
        {
            // Usually hcsctl is not installed; the raw Win32 message ("The system cannot find
            // the file specified") does not make that obvious.
            return ConnectAvailability.Failure(
                $"Could not start '{startInfo.FileName}': {ex.Message} " +
                "Check that hcsctl is installed and on PATH.");
        }

        return new ExecuteCommandResult
        {
            Success = true,
            Message = $"Opened an interactive '{shell}' session in the container.",
        };
    }

    /// <summary>
    /// <c>hcsctl.exe container exec --id ID --cmd SHELL --interactive --tty [--store PATH]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UseShellExecute</c> is required here, not stylistic: hcsctl's <c>--tty</c> checks that
    /// its own inherited stdin and stdout are attached terminals, and fails immediately
    /// ("--tty requires attached stdin and stdout terminals") if they are redirected. Only
    /// <c>UseShellExecute = true</c> gives the child real, inherited console handles.
    /// </para>
    /// <para>
    /// No <c>--json</c>/<c>--stream-json</c>: hcsctl rejects those combined with
    /// <c>--interactive</c> (exit 64), and interactive mode has no structured output to parse
    /// anyway — guest and hcsctl output both go straight to the inherited console.
    /// </para>
    /// </remarks>
    internal static ProcessStartInfo BuildShellStartInfo(HcsContainerResource resource, string shell)
    {
        string hcsctlPath = HcsCtlBinary.Locate(resource.HcsCtlPath);

        ProcessStartInfo startInfo = new(hcsctlPath) { UseShellExecute = true };

        startInfo.ArgumentList.Add("container");
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--id");
        startInfo.ArgumentList.Add(resource.ContainerId);
        startInfo.ArgumentList.Add("--cmd");
        startInfo.ArgumentList.Add(shell);
        startInfo.ArgumentList.Add("--interactive");
        startInfo.ArgumentList.Add("--tty");

        // Mirrors HcsCtl.InvokeAsync's own --store handling (Cli/HcsCtl.cs).
        if ((resource.StorePath ?? AspireHcsEnvironment.DefaultStorePath) is { Length: > 0 } store)
        {
            startInfo.ArgumentList.Add("--store");
            startInfo.ArgumentList.Add(store);
        }

        return startInfo;
    }

    /// <summary>
    /// Enabled only when the container is Running. A shell needs no address, unlike SSH/RDP.
    /// </summary>
    private static ResourceCommandState Availability(UpdateCommandStateContext context)
        => context.ResourceSnapshot.State?.Text == KnownResourceStates.Running
            ? ResourceCommandState.Enabled
            : ResourceCommandState.Disabled;
}
