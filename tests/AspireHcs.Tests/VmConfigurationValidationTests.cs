using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

/// <summary>
/// Drives <see cref="HcsVmOrchestrator.ValidateConfiguration"/>, the boot-start check for rules
/// that span builder methods. Each rule must fail before anything is created, and must name the
/// resource so a multi-VM apphost's error is attributable.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
public class VmConfigurationValidationTests
{
    private static IResourceBuilder<HcsVirtualMachineResource> Vm()
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm");

    [Fact]
    public void Endpoints_without_a_network_are_rejected()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithEndpoint("ssh", targetPort: 22);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HcsVmOrchestrator.ValidateConfiguration(vm.Resource));
        Assert.Contains("'vm'", ex.Message);
        Assert.Contains("WithNetwork", ex.Message);
    }

    [Fact]
    public void A_mac_without_a_network_is_rejected()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithMacAddress("00-15-5D-02-33-0E");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HcsVmOrchestrator.ValidateConfiguration(vm.Resource));
        Assert.Contains("MAC", ex.Message);
    }

    [Fact]
    public void A_vlan_without_a_network_is_rejected()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithVlan(10);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HcsVmOrchestrator.ValidateConfiguration(vm.Resource));
        Assert.Contains("VLAN", ex.Message);
    }

    [Fact]
    public void A_guest_address_without_a_network_is_rejected()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm().WithGuestAddress("10.20.10.20");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HcsVmOrchestrator.ValidateConfiguration(vm.Resource));
        Assert.Contains("guest address", ex.Message);
    }

    [Fact]
    public void An_agentless_vm_with_environment_values_is_rejected()
    {
        // WithEnvironment as a consumer needs the hcsguest agent to land the values in the
        // guest; an agentless VM by definition has none. The failure must come here, not after
        // a full boot at env-write time.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm()
            .WithNetwork()
            .WithGuestAddress("10.20.10.20")
            .WithEnvironment("KEY", "value");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => HcsVmOrchestrator.ValidateConfiguration(vm.Resource));
        Assert.Contains("agentless", ex.Message);
        Assert.Contains("WithGuestAddress", ex.Message);
    }

    [Fact]
    public void A_fully_configured_agentless_vm_passes()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm()
            .WithNetwork("LAB")
            .WithMacAddress("00-15-5D-02-33-0E")
            .WithVlan(10)
            .WithGuestAddress("10.20.10.20")
            .WithEndpoint("https", targetPort: 443, scheme: "https");

        HcsVmOrchestrator.ValidateConfiguration(vm.Resource);
    }

    [Fact]
    public void An_agent_path_vm_with_environment_values_passes()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm()
            .WithNetwork()
            .WithEnvironment("KEY", "value");

        HcsVmOrchestrator.ValidateConfiguration(vm.Resource);
    }
}
