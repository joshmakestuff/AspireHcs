using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// The connect commands launch a client on the host; these pin what is spawned and when the
// button is live. Process.Start itself is not covered: seeing a client window appear needs a
// human.
[SupportedOSPlatform("windows10.0.17763")]
public class ConnectCommandTests
{
    [Fact]
    public void Connect_commands_are_absent_unless_asked_for()
    {
        // They launch processes on the developer's desktop; opt-in only.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22);

        string[] names = [.. vm.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(a => a.Name)];

        Assert.DoesNotContain(ConnectCommands.SshCommandName, names);
        Assert.DoesNotContain(ConnectCommands.RdpCommandName, names);
    }

    [Fact]
    public void Connect_commands_are_registered_when_opted_in()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm()
            .WithEndpoint("ssh", 22)
            .WithEndpoint("rdp", 3389)
            .WithSshCommand(userName: "Administrator")
            .WithRdpCommand(userName: "Administrator");

        string[] names = [.. vm.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(a => a.Name)];

        Assert.Contains(ConnectCommands.SshCommandName, names);
        Assert.Contains(ConnectCommands.RdpCommandName, names);
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("rdp")]
    public void Naming_an_undeclared_endpoint_fails_the_model_build(string kind)
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => _ = kind == "ssh"
                ? vm.WithSshCommand("typo")
                : vm.WithRdpCommand("typo"));

        Assert.Contains("has no endpoint named 'typo'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithEndpoint(\"typo\", targetPort)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrepresentable_user_name_fails_the_model_build_not_the_click()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("rdp", 3389);

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => vm.WithRdpCommand(userName: "Administrator\r\nfull address:s:evil.example"));

        Assert.Contains("control character", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Not up yet: nothing to connect to.
    [InlineData("NotStarted", false, ResourceCommandState.Disabled)]
    [InlineData("Starting", false, ResourceCommandState.Disabled)]
    // No state published yet; the case LifecycleCommandTests covers for Start.
    [InlineData(null, false, ResourceCommandState.Disabled)]
    [InlineData(null, true, ResourceCommandState.Disabled)]
    // On the way down, the allocation is still present but the guest is going away.
    [InlineData("Stopping", true, ResourceCommandState.Disabled)]
    // Running, but the DHCP lease has not surfaced yet; a connect attempt fails in this window.
    [InlineData("Running", false, ResourceCommandState.Disabled)]
    // Running with an address is the only state that can connect.
    [InlineData("Running", true, ResourceCommandState.Enabled)]
    // A stale allocation from the previous run must not re-enable the button after a stop.
    [InlineData("Exited", true, ResourceCommandState.Disabled)]
    [InlineData("FailedToStart", true, ResourceCommandState.Disabled)]
    public void Availability_needs_both_running_and_an_allocated_address(
        string? state, bool allocated, ResourceCommandState expected)
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm()
            .WithEndpoint("ssh", 22)
            .WithEndpoint("rdp", 3389)
            .WithSshCommand()
            .WithRdpCommand();

        if (allocated)
        {
            Allocate(vm, "ssh", "192.168.1.20", 22);
            Allocate(vm, "rdp", "192.168.1.20", 3389);
        }

        Assert.Equal(expected, Evaluate(vm, ConnectCommands.SshCommandName, state));
        Assert.Equal(expected, Evaluate(vm, ConnectCommands.RdpCommandName, state));
    }

    [Fact]
    public void Ssh_command_line_carries_the_port_and_uses_dash_l_for_the_user()
    {
        ProcessStartInfo startInfo = ConnectCommands.BuildSshStartInfo("192.168.1.20", 2222, "Administrator");

        Assert.Equal("ssh.exe", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        // -l, not Administrator@192.168.1.20: the user name never meets a delimiter it could
        // itself contain.
        Assert.Equal(["-p", "2222", "-l", "Administrator", "192.168.1.20"], startInfo.ArgumentList);
    }

    [Fact]
    public void Ssh_command_line_omits_the_user_entirely_when_unset()
    {
        ProcessStartInfo startInfo = ConnectCommands.BuildSshStartInfo("192.168.1.20", 22, userName: null);

        Assert.Equal(["-p", "22", "192.168.1.20"], startInfo.ArgumentList);
    }

    [Fact]
    public void Rdp_file_names_the_address_and_the_user()
    {
        string content = RdpFile.Build("192.168.1.20", 3389, "Administrator");

        Assert.Contains("full address:s:192.168.1.20:3389", content, StringComparison.Ordinal);
        Assert.Contains("username:s:Administrator", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Rdp_file_omits_the_user_line_when_unset()
    {
        // Absent, not empty: `username:s:` prefills a blank user instead of the last one used.
        string content = RdpFile.Build("192.168.1.20", 3389, userName: null);

        Assert.DoesNotContain("username", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Rdp_file_brackets_ipv6_so_the_port_stays_distinguishable()
    {
        Assert.Equal("[fd00::20]:3389", RdpFile.FormatFullAddress("fd00::20", 3389));
        Assert.Equal("192.168.1.20:3389", RdpFile.FormatFullAddress("192.168.1.20", 3389));
        // A host name is not an IPv6 literal even though it is not an IPv4 one either.
        Assert.Equal("guest.local:3389", RdpFile.FormatFullAddress("guest.local", 3389));
    }

    [Theory]
    [InlineData("has\rcarriage")]
    [InlineData("has\nnewline")]
    [InlineData("has\0nul")]
    [InlineData("has\ttab")]
    public void Rdp_values_that_would_break_out_of_a_line_are_rejected(string value)
    {
        Assert.Throws<ArgumentException>(() => RdpFile.Field("username", 's', value));
    }

    [Fact]
    public void Rdp_file_is_utf16le_with_a_bom_like_mstsc_writes()
    {
        Assert.Equal([0xFF, 0xFE], RdpFile.FileEncoding.GetPreamble());
        Assert.Equal("A\0", Encoding.Latin1.GetString(RdpFile.FileEncoding.GetBytes("A")));
    }

    [Fact]
    public void A_click_before_the_lease_arrives_reports_why_and_launches_nothing()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: "Running",
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            launched.Add);

        Assert.False(result.Success);
        Assert.Contains("has no address yet", result.Message, StringComparison.Ordinal);
        Assert.Empty(launched);
    }

    [Fact]
    public void A_click_from_a_non_interactive_apphost_reports_why_and_launches_nothing()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: false, currentState: "Running",
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            launched.Add);

        Assert.False(result.Success);
        Assert.Contains("not running in an interactive session", result.Message, StringComparison.Ordinal);
        Assert.Empty(launched);
    }

    [Fact]
    public void A_failing_launch_is_reported_rather_than_thrown()
    {
        // An unhandled exception out of a command handler is the one outcome the dashboard
        // cannot render usefully.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: "Running",
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            _ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified"));

        Assert.False(result.Success);
        Assert.Contains("ssh.exe", result.Message, StringComparison.Ordinal);
        Assert.Contains("installed and on PATH", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_click_launches_the_client_at_the_leased_address()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: "Running",
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, "Administrator"),
            launched.Add);

        Assert.True(result.Success);
        ProcessStartInfo startInfo = Assert.Single(launched);
        Assert.Equal("ssh.exe", startInfo.FileName);
        Assert.Equal(["-p", "22", "-l", "Administrator", "192.168.1.20"], startInfo.ArgumentList);
    }

    [Fact]
    public void An_ssh_click_prefers_a_running_forward_over_the_leased_address()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);
        vm.Resource.ForwardedConnectAddresses["ssh"] = "127.0.0.1:54321";
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: "Running",
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            launched.Add);

        Assert.True(result.Success);
        Assert.Contains("127.0.0.1:54321", result.Message, StringComparison.Ordinal);
        ProcessStartInfo startInfo = Assert.Single(launched);
        Assert.Equal(["-p", "54321", "127.0.0.1"], startInfo.ArgumentList);
    }

    [Fact]
    public void An_ssh_click_falls_back_to_the_leased_address_once_the_forward_is_gone()
    {
        // The forward died mid-session (or the agent was never present); the leased address is
        // still there, exactly as it was before the forward existed.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: "Running",
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            launched.Add);

        Assert.True(result.Success);
        ProcessStartInfo startInfo = Assert.Single(launched);
        Assert.Equal(["-p", "22", "192.168.1.20"], startInfo.ArgumentList);
    }

    [Fact]
    public void An_rdp_click_prefers_a_running_forward_over_the_leased_address()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("rdp", 3389).WithRdpCommand();
        Allocate(vm, "rdp", "192.168.1.20", 3389);
        vm.Resource.ForwardedConnectAddresses["rdp"] = "127.0.0.1:54321";
        List<ProcessStartInfo> launched = [];
        string path = ConnectCommands.RdpFilePath(vm.Resource, "rdp");

        try
        {
            ExecuteCommandResult result = ConnectCommands.Execute(
                vm.Resource, "rdp", "a Remote Desktop session", userInteractive: true, currentState: "Running",
                preferForward: true,
                (address, port) => ConnectCommands.BuildRdpStartInfo(vm.Resource, "rdp", address, port, null),
                launched.Add);

            Assert.True(result.Success);
            Assert.Contains("127.0.0.1:54321", result.Message, StringComparison.Ordinal);

            string content = File.ReadAllText(path, RdpFile.FileEncoding);
            Assert.Contains("full address:s:127.0.0.1:54321", content, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_rdp_click_falls_back_to_the_leased_address_once_the_forward_is_gone()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("rdp", 3389).WithRdpCommand();
        Allocate(vm, "rdp", "192.168.1.20", 3389);
        List<ProcessStartInfo> launched = [];
        string path = ConnectCommands.RdpFilePath(vm.Resource, "rdp");

        try
        {
            ExecuteCommandResult result = ConnectCommands.Execute(
                vm.Resource, "rdp", "a Remote Desktop session", userInteractive: true, currentState: "Running",
                preferForward: true,
                (address, port) => ConnectCommands.BuildRdpStartInfo(vm.Resource, "rdp", address, port, null),
                launched.Add);

            Assert.True(result.Success);
            Assert.Contains("192.168.1.20:3389", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void With_ssh_command_records_the_endpoints_target_port_for_the_forward_pump()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();

        Assert.Equal(22, vm.Resource.HvsocketForwardTargets["ssh"]);
    }

    [Fact]
    public void With_rdp_command_records_the_endpoints_target_port_for_the_forward_pump()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("rdp", 3389).WithRdpCommand();

        Assert.Equal(3389, vm.Resource.HvsocketForwardTargets["rdp"]);
    }

    [Fact]
    public void The_rdp_click_writes_a_connection_file_and_hands_it_to_mstsc()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("rdp", 3389).WithRdpCommand();
        Allocate(vm, "rdp", "192.168.1.20", 3389);
        List<ProcessStartInfo> launched = [];
        string path = ConnectCommands.RdpFilePath(vm.Resource, "rdp");

        try
        {
            ExecuteCommandResult result = ConnectCommands.Execute(
                vm.Resource, "rdp", "a Remote Desktop session", userInteractive: true, currentState: "Running",
                preferForward: true,
                (address, port) => ConnectCommands.BuildRdpStartInfo(
                    vm.Resource, "rdp", address, port, "Administrator"),
                launched.Add);

            Assert.True(result.Success);
            ProcessStartInfo startInfo = Assert.Single(launched);
            Assert.Equal("mstsc.exe", startInfo.FileName);
            Assert.Equal([path], startInfo.ArgumentList);

            Assert.True(File.Exists(path));
            string content = File.ReadAllText(path, RdpFile.FileEncoding);
            Assert.Contains("full address:s:192.168.1.20:3389", content, StringComparison.Ordinal);
            Assert.Contains("username:s:Administrator", content, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Exited")]
    [InlineData("FailedToStart")]
    [InlineData("Finished")]
    // Not terminal, but not connectable either. During Starting a restart still carries the
    // previous run's allocation: the stale-address case.
    [InlineData("Starting")]
    [InlineData("Stopping")]
    public void A_click_on_a_vm_that_is_not_running_is_refused_even_though_the_allocation_survives(string state)
    {
        // UpdateState only governs what the dashboard offers; the command is still reachable
        // through Aspire's command APIs, and HcsVmOrchestrator never clears AllocatedEndpoint.
        // Without this guard the client launches at the previous run's address.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: state,
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            launched.Add);

        Assert.False(result.Success);
        Assert.Contains("nothing to connect to", result.Message, StringComparison.Ordinal);
        Assert.Empty(launched);
    }

    [Fact]
    public void An_unknown_state_is_allowed_through_rather_than_refused()
    {
        // The resource id the state is looked up by is not guaranteed to equal the resource
        // name. A lookup miss must not disable the command.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithSshCommand();
        Allocate(vm, "ssh", "192.168.1.20", 22);
        List<ProcessStartInfo> launched = [];

        ExecuteCommandResult result = ConnectCommands.Execute(
            vm.Resource, "ssh", "an SSH session", userInteractive: true, currentState: null,
            preferForward: true,
            (address, port) => ConnectCommands.BuildSshStartInfo(address, port, null),
            launched.Add);

        Assert.True(result.Success);
        Assert.Single(launched);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_explicitly_empty_user_name_is_rejected_rather_than_treated_as_unset(string userName)
    {
        // null means "unspecified"; empty means an account was meant and none named, and a
        // fallback to the host account would connect as the wrong user.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", 22).WithEndpoint("rdp", 3389);

        Assert.Throws<ArgumentException>(() => vm.WithSshCommand(userName: userName));
        Assert.Throws<ArgumentException>(() => vm.WithRdpCommand(userName: userName));
    }

    [Theory]
    [InlineData(@"..\..\..\evil")]
    [InlineData("../../../evil")]
    [InlineData(@"..\..\..\..\..\evil")]
    public void An_endpoint_name_that_would_escape_the_connect_directory_is_refused(string endpointName)
    {
        // The endpoint name is interpolated into a file name, so it crosses into path syntax.
        // Three levels, not two: the name is suffixed onto the VmId, so `aspirehcs-vm-x-..` is
        // one ordinary segment (Windows strips the trailing dots) that the following `..` pops.
        // The boundary case is below.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConnectCommands.RdpFilePath(vm.Resource, endpointName));

        Assert.Contains("outside", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_traversal_levels_are_absorbed_by_the_vm_id_prefix_and_stay_inside()
    {
        // `..\..\evil` lands on connect\evil.rdp, still inside. The guard asserts containment,
        // not the absence of `..`, so this passes. Pins the file-name shape.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        string path = ConnectCommands.RdpFilePath(vm.Resource, @"..\..\evil");

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "AspireHcs", "connect", "evil.rdp"),
            path);
    }

    [Fact]
    public void A_normal_endpoint_name_stays_inside_the_connect_directory()
    {
        // The negative above is only meaningful if the positive passes.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        string path = ConnectCommands.RdpFilePath(vm.Resource, "rdp");

        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "AspireHcs", "connect"), path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("-rdp.rdp", path, StringComparison.Ordinal);
    }

    private static void Allocate(
        IResourceBuilder<HcsVirtualMachineResource> vm, string endpointName, string address, int port)
    {
        EndpointAnnotation endpoint = vm.Resource.Annotations.OfType<EndpointAnnotation>()
            .Single(e => e.Name == endpointName);
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, address, port);
    }

    private static ResourceCommandState Evaluate(
        IResourceBuilder<HcsVirtualMachineResource> vm, string commandName, string? state)
    {
        ResourceCommandAnnotation command = vm.Resource.Annotations
            .OfType<ResourceCommandAnnotation>().Single(a => a.Name == commandName);

        CustomResourceSnapshot snapshot = new()
        {
            ResourceType = "HcsVirtualMachine",
            Properties = [],
            State = state is null ? null : new ResourceStateSnapshot(state, null),
        };

        return command.UpdateState(new UpdateCommandStateContext { ResourceSnapshot = snapshot, Services = null! });
    }

    private static IResourceBuilder<HcsVirtualMachineResource> Vm()
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm").WithNetwork();
}
