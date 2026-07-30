using System.Runtime.Versioning;

// This AppHost drives Hyper-V VMs and is Windows-only by nature.
[assembly: SupportedOSPlatform("windows10.0.17763")]

var builder = DistributedApplication.CreateBuilder(args);

string vhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX")
    ?? throw new InvalidOperationException(
        "Set the HCS_TEST_VHDX environment variable to the path of a bootable Gen2/UEFI VHDX.");

builder.AddHcsVm("appliance")
    .WithVhdx(vhdx, copyOnWrite: true)
    .WithMemory(gigabytes: 2)
    .WithProcessorCount(2)
    .WithNatNetwork()
    .WithEndpoint("ssh", targetPort: 22);

builder.Build().Run();
