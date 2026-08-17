using System.Runtime.Versioning;

// Hyper-V VMs and Windows containers; Windows-only.
[assembly: SupportedOSPlatform("windows10.0.17763")]

var builder = DistributedApplication.CreateBuilder(args);

// Every resource is opt-in through an environment variable, because each needs a fixture that
// is not in the repository: a bootable VHDX per VM, and an image already imported into an hcsctl
// store for the container. See README.md beside this file for how to prepare each fixture.
//
//   HCS_TEST_VHDX             Linux VM  ("appliance")
//   HCS_TEST_VM_USER          SSH account for the Linux VM (default: root)
//   HCS_SAMPLE_WINDOWS_VHDX   Windows VM ("winserver")
//   HCS_SAMPLE_WINDOWS_USER   RDP/SSH account for the Windows VM (default: Administrator)
//   ASPIREHCS_TEST_IMAGE      Windows container ("worker"): image reference in the store
//   ASPIREHCS_TEST_COMMAND    Container command (default: a long-running ping)
//   ASPIREHCS_TEST_STORE      hcsctl store used by all three (default: hcsctl's per-user store)
//   ASPIREHCS_HCSCTL          Path to hcsctl.exe when it is not on PATH

string? store = Environment.GetEnvironmentVariable("ASPIREHCS_TEST_STORE") is { Length: > 0 } s ? s : null;

// ---- Linux VM -------------------------------------------------------------------------------
// Fixture: a Gen2/UEFI VHDX with a Linux OS installed, the hcsguest agent running (systemd),
// NIC on DHCP, and sshd enabled. Reference fixture: Rocky Linux 10, root only.
string? linuxVhdx = Environment.GetEnvironmentVariable("HCS_TEST_VHDX");
if (!string.IsNullOrWhiteSpace(linuxVhdx))
{
    var appliance = builder.AddHcsVm("appliance")
        .WithVhdx(linuxVhdx)
        .WithMemory(gigabytes: 2)
        .WithProcessorCount(2)
        .WithNetwork()
        .WithEndpoint("ssh", targetPort: 22)
        // Dashboard "Connect (SSH)" button. The account must exist in the image. Add
        // .WithTcpHealthCheck("ssh") to make readiness wait for sshd; see the Windows VM below.
        .WithSshCommand(userName: Environment.GetEnvironmentVariable("HCS_TEST_VM_USER") ?? "root");

    if (store is not null)
    {
        appliance.WithHcsCtl(storePath: store);
    }
}

// ---- Windows VM -----------------------------------------------------------------------------
// Fixture: a Gen2/UEFI VHDX with Windows Server (or client) installed, the hcsguest agent
// running as a service, NIC on DHCP, Remote Desktop enabled and its firewall group opened, and
// the local account below able to log on.
string? windowsVhdx = Environment.GetEnvironmentVariable("HCS_SAMPLE_WINDOWS_VHDX");
if (!string.IsNullOrWhiteSpace(windowsVhdx))
{
    string windowsUser = Environment.GetEnvironmentVariable("HCS_SAMPLE_WINDOWS_USER") ?? "Administrator";

    var winserver = builder.AddHcsVm("winserver")
        .WithVhdx(windowsVhdx)
        .WithMemory(gigabytes: 4)
        .WithProcessorCount(2)
        .WithNetwork()
        .WithEndpoint("rdp", targetPort: 3389)
        // Healthy once TermService accepts a connection on 3389.
        .WithTcpHealthCheck("rdp")
        // Dashboard "Connect (RDP)" button: opens mstsc with the leased address and this user.
        .WithRdpCommand(userName: windowsUser);

    if (store is not null)
    {
        winserver.WithHcsCtl(storePath: store);
    }
}

// ---- Windows container ----------------------------------------------------------------------
// Fixture: an image imported into an hcsctl store (elevated, once):
//   hcsctl image pull   --ref <ref> --store <dir>
//   hcsctl image import --ref <ref> --store <dir>
string? image = Environment.GetEnvironmentVariable("ASPIREHCS_TEST_IMAGE");
if (!string.IsNullOrWhiteSpace(image))
{
    var worker = builder.AddHcsContainer("worker")
        .WithImage(image)
        // A long-running default keeps the resource Running; a one-shot command reaches Finished
        // as soon as it exits.
        .WithCommand(Environment.GetEnvironmentVariable("ASPIREHCS_TEST_COMMAND") ?? "cmd /c ping -t 127.0.0.1");

    if (store is not null)
    {
        worker.WithStore(store);
    }
}

if (linuxVhdx is null or { Length: 0 } && windowsVhdx is null or { Length: 0 } && image is null or { Length: 0 })
{
    throw new InvalidOperationException(
        "Nothing to run. Set HCS_TEST_VHDX (Linux VM) or HCS_SAMPLE_WINDOWS_VHDX (Windows VM) to a bootable " +
        "Gen2/UEFI VHDX, or ASPIREHCS_TEST_IMAGE to an image reference already imported into an hcsctl store " +
        "(with ASPIREHCS_TEST_STORE naming that store). See samples/HcsSample.AppHost/README.md.");
}

builder.Build().Run();
