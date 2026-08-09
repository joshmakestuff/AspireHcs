using System.Runtime.Versioning;

// This AppHost drives Hyper-V VMs and Windows containers, and is Windows-only by nature.
[assembly: SupportedOSPlatform("windows10.0.17763")]

var builder = DistributedApplication.CreateBuilder(args);

// Both resources are opt-in, because each needs a fixture that cannot be committed: a bootable
// VHDX for the VM, and an image already imported into an hcsctl store for the container. Adding
// them conditionally lets one sample serve either demo, and lets the integration suite pick one
// without paying for the other.

string? vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
if (!string.IsNullOrWhiteSpace(vhdx))
{
    IResourceBuilder<HcsVirtualMachineResource> appliance = builder.AddHcsVm("appliance")
        .WithVhdx(vhdx)
        .WithMemory(gigabytes: 2)
        .WithProcessorCount(2)
        .WithNatNetwork()
        .WithEndpoint("ssh", targetPort: 22)
        // Administrator is the account the guest-image fixture ships. There is no matching
        // WithRdpCommand here because that image does not serve RDP — a connect button that
        // cannot connect is worse than no button.
        .WithSshCommand(userName: "Administrator");

    // The VM path drives hcsctl too now, so it takes the same store the container half does. A
    // VM store holds differencing disks rather than images, so pointing both at one directory is
    // fine and keeps a run's leftovers in one place.
    if (Environment.GetEnvironmentVariable("ASPIREHCS_TEST_STORE") is { Length: > 0 } vmStore)
    {
        appliance.WithHcsCtl(storePath: vmStore);
    }
}

string? image = Environment.GetEnvironmentVariable("ASPIREHCS_TEST_IMAGE");
if (!string.IsNullOrWhiteSpace(image))
{
    var container = builder.AddHcsContainer("worker")
        .WithImage(image)
        // Defaults to a long-running command so the resource stays Running rather than reaching
        // Finished the moment a one-shot exits.
        .WithCommand(Environment.GetEnvironmentVariable("ASPIREHCS_TEST_COMMAND") ?? "cmd /c ping -t 127.0.0.1");

    // The image is imported out of band — `hcsctl image import` is elevated — so the store is
    // almost never hcsctl's per-user default.
    if (Environment.GetEnvironmentVariable("ASPIREHCS_TEST_STORE") is { Length: > 0 } store)
    {
        container.WithStore(store);
    }
}

if (vhdx is null or { Length: 0 } && image is null or { Length: 0 })
{
    throw new InvalidOperationException(
        "Nothing to run. Set HCS_TEST_VHDX to a bootable Gen2/UEFI VHDX, or ASPIREHCS_TEST_IMAGE to an " +
        "image reference already imported into an hcsctl store (with ASPIREHCS_TEST_STORE naming that store).");
}

builder.Build().Run();
