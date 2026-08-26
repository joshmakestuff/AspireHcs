using System.Runtime.Versioning;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace AspireHcs.Tests;

// Aspire wires Start/Stop/Restart only for resources DCP owns (ContainerCreator/ExecutableCreator
// call AddLifeCycleCommands), so an HCS VM gets none unless the integration adds them. These pin
// which command is offered in which state.
[SupportedOSPlatform("windows10.0.17763")]
public class LifecycleCommandTests
{
    [Fact]
    public void Lifecycle_commands_are_registered_under_the_known_names()
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        string[] names = [.. vm.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(a => a.Name)];

        Assert.Contains(KnownResourceCommands.StartCommand, names);
        Assert.Contains(KnownResourceCommands.StopCommand, names);
        Assert.Contains(KnownResourceCommands.RestartCommand, names);
    }

    [Fact]
    public void State_names_used_below_still_match_Aspire()
    {
        // KnownResourceStates members are static readonly, not const, so the cases below spell
        // them as literals. This catches an upstream rename.
        Assert.Equal("NotStarted", KnownResourceStates.NotStarted);
        Assert.Equal("Starting", KnownResourceStates.Starting);
        Assert.Equal("Running", KnownResourceStates.Running);
        Assert.Equal("Stopping", KnownResourceStates.Stopping);
        Assert.Equal("Exited", KnownResourceStates.Exited);
        Assert.Equal("FailedToStart", KnownResourceStates.FailedToStart);
    }

    [Theory]
    // Nothing is running: only Start is on offer.
    [InlineData("NotStarted", ResourceCommandState.Enabled, ResourceCommandState.Hidden, ResourceCommandState.Disabled)]
    [InlineData("Exited", ResourceCommandState.Enabled, ResourceCommandState.Hidden, ResourceCommandState.Disabled)]
    [InlineData("FailedToStart", ResourceCommandState.Enabled, ResourceCommandState.Hidden, ResourceCommandState.Disabled)]
    // Mid-flight: neither direction may be re-issued.
    [InlineData("Starting", ResourceCommandState.Disabled, ResourceCommandState.Hidden, ResourceCommandState.Disabled)]
    [InlineData("Stopping", ResourceCommandState.Disabled, ResourceCommandState.Disabled, ResourceCommandState.Disabled)]
    // Up: stopping and restarting are the only things left to do.
    [InlineData("Running", ResourceCommandState.Hidden, ResourceCommandState.Enabled, ResourceCommandState.Enabled)]
    public void Command_availability_follows_the_resource_state(
        string state, ResourceCommandState start, ResourceCommandState stop, ResourceCommandState restart)
    {
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        Assert.Equal(start, Evaluate(vm, KnownResourceCommands.StartCommand, state));
        Assert.Equal(stop, Evaluate(vm, KnownResourceCommands.StopCommand, state));
        Assert.Equal(restart, Evaluate(vm, KnownResourceCommands.RestartCommand, state));
    }

    [Fact]
    public void Start_is_offered_before_any_state_has_been_published()
    {
        // A resource whose snapshot has no state text yet must still be startable from the
        // dashboard.
        IResourceBuilder<HcsVirtualMachineResource> vm = Vm();

        Assert.Equal(ResourceCommandState.Enabled, Evaluate(vm, KnownResourceCommands.StartCommand, state: null));
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
        => DistributedApplication.CreateBuilder([]).AddHcsVm("vm");
}
