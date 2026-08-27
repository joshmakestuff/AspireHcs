using System.Diagnostics;
using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// The shell connect command launches hcsctl on the host; these pin what is spawned and when the
// button is live. Process.Start itself is not covered: seeing a console window appear needs a
// human. BuildShellStartInfo's own tests need a real hcsctl.exe to resolve against and are
// skippable; the Execute orchestration tests inject a stub build delegate and need nothing.
[SupportedOSPlatform("windows10.0.17763")]
public class ContainerConnectCommandTests
{
    [Fact]
    public void Connect_shell_command_is_absent_unless_asked_for()
    {
        // It launches a process on the developer's desktop; opt-in only.
        IResourceBuilder<HcsContainerResource> container = Container();

        string[] names = [.. container.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(a => a.Name)];

        Assert.DoesNotContain(ContainerConnectCommands.ShellCommandName, names);
    }

    [Fact]
    public void Connect_shell_command_is_registered_when_opted_in()
    {
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();

        string[] names = [.. container.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(a => a.Name)];

        Assert.Contains(ContainerConnectCommands.ShellCommandName, names);
    }

    [Theory]
    // Not up yet: nothing to attach a shell to.
    [InlineData("NotStarted", ResourceCommandState.Disabled)]
    [InlineData("Starting", ResourceCommandState.Disabled)]
    // No state published yet; unknown is treated as "not yet running" for what the dashboard offers.
    [InlineData(null, ResourceCommandState.Disabled)]
    [InlineData("Stopping", ResourceCommandState.Disabled)]
    // Running is the only enabled state: a container has no address to also wait for.
    [InlineData("Running", ResourceCommandState.Enabled)]
    [InlineData("Exited", ResourceCommandState.Disabled)]
    [InlineData("FailedToStart", ResourceCommandState.Disabled)]
    public void Availability_needs_only_running_no_address_gate(string? state, ResourceCommandState expected)
    {
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();

        Assert.Equal(expected, Evaluate(container, state));
    }

    [SkippableFact]
    public void Shell_command_line_uses_cmd_exe_by_default()
    {
        Skip.IfNot(RepositoryTools.TryFindHcsCtl(out string? hcsctlPath, out string? failure), failure);

        IResourceBuilder<HcsContainerResource> container = Container().WithHcsCtl(hcsctlPath);

        ProcessStartInfo startInfo = ContainerConnectCommands.BuildShellStartInfo(container.Resource, "cmd.exe");

        Assert.Equal(hcsctlPath, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(
            ["container", "exec", "--id", container.Resource.ContainerId, "--cmd", "cmd.exe", "--interactive", "--tty"],
            startInfo.ArgumentList);
    }

    [SkippableFact]
    public void Shell_command_line_honors_an_overridden_shell()
    {
        Skip.IfNot(RepositoryTools.TryFindHcsCtl(out string? hcsctlPath, out string? failure), failure);

        IResourceBuilder<HcsContainerResource> container = Container().WithHcsCtl(hcsctlPath);

        ProcessStartInfo startInfo = ContainerConnectCommands.BuildShellStartInfo(container.Resource, "powershell.exe");

        Assert.Contains("powershell.exe", startInfo.ArgumentList);
        Assert.DoesNotContain("cmd.exe", startInfo.ArgumentList);
    }

    [SkippableFact]
    public void Shell_command_line_appends_store_when_configured()
    {
        Skip.IfNot(RepositoryTools.TryFindHcsCtl(out string? hcsctlPath, out string? failure), failure);

        IResourceBuilder<HcsContainerResource> container = Container().WithHcsCtl(hcsctlPath).WithStore(@"C:\store");

        ProcessStartInfo startInfo = ContainerConnectCommands.BuildShellStartInfo(container.Resource, "cmd.exe");

        Assert.Equal(
            [
                "container", "exec", "--id", container.Resource.ContainerId, "--cmd", "cmd.exe",
                "--interactive", "--tty", "--store", Path.GetFullPath(@"C:\store"),
            ],
            startInfo.ArgumentList);
    }

    [SkippableFact]
    public void Shell_command_line_omits_store_when_unset()
    {
        Skip.IfNot(RepositoryTools.TryFindHcsCtl(out string? hcsctlPath, out string? failure), failure);

        string? originalStore = Environment.GetEnvironmentVariable("ASPIREHCS_STORE");
        Environment.SetEnvironmentVariable("ASPIREHCS_STORE", null);
        try
        {
            IResourceBuilder<HcsContainerResource> container = Container().WithHcsCtl(hcsctlPath);

            ProcessStartInfo startInfo = ContainerConnectCommands.BuildShellStartInfo(container.Resource, "cmd.exe");

            Assert.DoesNotContain("--store", startInfo.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPIREHCS_STORE", originalStore);
        }
    }

    [Fact]
    public void A_click_on_a_container_that_is_not_running_is_refused()
    {
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ContainerConnectCommands.Execute(
            container.Resource, "cmd.exe", userInteractive: true, currentState: "Starting",
            Stub, launched.Add);

        Assert.False(result.Success);
        Assert.Contains("nothing to attach a shell to", result.Message, StringComparison.Ordinal);
        Assert.Empty(launched);
    }

    [Fact]
    public void A_click_from_a_non_interactive_apphost_reports_why_and_launches_nothing()
    {
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ContainerConnectCommands.Execute(
            container.Resource, "cmd.exe", userInteractive: false, currentState: "Running",
            Stub, launched.Add);

        Assert.False(result.Success);
        Assert.Contains("not running in an interactive session", result.Message, StringComparison.Ordinal);
        Assert.Empty(launched);
    }

    [Fact]
    public void A_failing_launch_is_reported_rather_than_thrown()
    {
        // An unhandled exception out of a command handler is the one outcome the dashboard
        // cannot render usefully.
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();

        ExecuteCommandResult result = ContainerConnectCommands.Execute(
            container.Resource, "cmd.exe", userInteractive: true, currentState: "Running",
            Stub,
            _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified"));

        Assert.False(result.Success);
        Assert.Contains("hcsctl.exe", result.Message, StringComparison.Ordinal);
        Assert.Contains("installed and on PATH", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_click_launches_the_built_argv()
    {
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ContainerConnectCommands.Execute(
            container.Resource, "cmd.exe", userInteractive: true, currentState: "Running",
            Stub, launched.Add);

        Assert.True(result.Success);
        ProcessStartInfo startInfo = Assert.Single(launched);
        Assert.Equal("hcsctl.exe", startInfo.FileName);
    }

    [Fact]
    public void An_unknown_state_is_allowed_through_rather_than_refused()
    {
        // The resource id the state is looked up by is not guaranteed to equal the resource
        // name. A lookup miss must not disable the command.
        IResourceBuilder<HcsContainerResource> container = Container().WithShellCommand();
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ContainerConnectCommands.Execute(
            container.Resource, "cmd.exe", userInteractive: true, currentState: null,
            Stub, launched.Add);

        Assert.True(result.Success);
        Assert.Single(launched);
    }

    private static ProcessStartInfo Stub(HcsContainerResource resource, string shell)
        => new("hcsctl.exe") { UseShellExecute = true };

    private static ResourceCommandState Evaluate(IResourceBuilder<HcsContainerResource> container, string? state)
    {
        ResourceCommandAnnotation command = container.Resource.Annotations
            .OfType<ResourceCommandAnnotation>().Single(a => a.Name == ContainerConnectCommands.ShellCommandName);

        CustomResourceSnapshot snapshot = new()
        {
            ResourceType = "HcsContainer",
            Properties = [],
            State = state is null ? null : new ResourceStateSnapshot(state, null),
        };

        return command.UpdateState(new UpdateCommandStateContext { ResourceSnapshot = snapshot, Services = null! });
    }

    private static IResourceBuilder<HcsContainerResource> Container()
        => DistributedApplication.CreateBuilder([]).AddHcsContainer("c");
}
