using System.Runtime.Versioning;

// Hyper-V VMs and Windows containers; Windows-only.
[assembly: SupportedOSPlatform("windows10.0.17763")]

var builder = DistributedApplication.CreateBuilder(args);

// Settings come from the "Hcs" section of appsettings.json (or user secrets), with an
// environment variable fallback. Only the VMs need any of them: they require a bootable VHDX
// that cannot ship with the repository. The container runs with no settings at all once
// prepare.ps1 has imported the default image.
string? Setting(string key, string environmentVariable)
    => builder.Configuration[$"Hcs:{key}"] is { Length: > 0 } fromConfig
        ? fromConfig
        : Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : null;

// ---- Windows container ----------------------------------------------------------------------
// A stock nanoserver image runs HcsSample.GuestApi straight from a bind-mounted publish
// directory: your binary, built on the host, hardware-isolated — no Dockerfile, no image build.
// prepare.ps1 publishes the app and imports the image (once, elevated).
string image = Setting("ContainerImage", "ASPIREHCS_TEST_IMAGE")
    ?? "mcr.microsoft.com/windows/nanoserver:ltsc2025";

string guestApiPublish = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "HcsSample.GuestApi", "bin", "publish"));
if (!Directory.Exists(guestApiPublish))
{
    throw new InvalidOperationException(
        $"'{guestApiPublish}' does not exist. Run samples\\prepare.ps1 once: it publishes " +
        "HcsSample.GuestApi and imports the container image into the store.");
}

// The repo pins hcsctl in tools\hcsctl (eng\Get-HcsCtl.ps1; prepare.ps1 fetches it). Falling
// back to it means a fresh clone needs no PATH entry and no environment variable. The fallback
// is used only when the ordinary resolution (ASPIREHCS_HCSCTL, then PATH) would find nothing,
// so a deliberate override still wins.
string pinnedHcsCtl = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "tools", "hcsctl", "hcsctl.exe"));
bool ordinaryResolutionWorks =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPIREHCS_HCSCTL"))
    || (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(dir => dir.IndexOfAny(Path.GetInvalidPathChars()) < 0 && File.Exists(Path.Combine(dir, "hcsctl.exe")));
string? repoHcsCtl = !ordinaryResolutionWorks && File.Exists(pinnedHcsCtl) ? pinnedHcsCtl : null;

// The image store lives beside the sample, not in per-user AppData: it is discoverable,
// travels with the clone, and deleting it deletes everything the sample materialized.
// prepare.ps1 imports into the same default; explicit config still wins.
string store = Setting("Store", "ASPIREHCS_STORE")
    ?? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".store"));

var worker = builder.AddHcsContainer("worker")
    .WithImage(image)
    // Both mounts are read-only in the guest and still live — a file edited on the host
    // changes in the guest without a restart. Read-only matters beyond hygiene: a writable
    // VSMB mount makes HCS refuse pause (0x80070032), and pause/resume is part of the demo.
    .WithBindMount(guestApiPublish, @"C:\app", isReadOnly: true)
    .WithBindMount("data", @"C:\data", isReadOnly: true)
    .WithCommand(@"C:\app\HcsSample.GuestApi.exe")
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:8080")
    .WithEnvironment("DATA_DIR", @"C:\data")
    .WithEnvironment("GREETING", "Hello from a Hyper-V-isolated container")
    .WithNetwork()
    .WithEndpoint("http", targetPort: 8080, scheme: "http")
    .WithTcpHealthCheck()
    .WithStore(store);

if (repoHcsCtl is not null)
{
    worker.WithHcsCtl(repoHcsCtl);
}

// ---- Frontend -------------------------------------------------------------------------------
// An ordinary Aspire project consuming the guests. Each referenced endpoint arrives as
// service-discovery configuration; the page shows what the container answers and probes the
// VMs' endpoints.
var web = builder.AddProject<Projects.HcsSample_Web>("web")
    .WithReference(worker.GetEndpoint("http"))
    .WaitFor(worker);

// ---- Consumer direction (opt-in) ------------------------------------------------------------
// The container consuming the web project: WithReference on an HCS resource delivers the
// endpoint into the guest, relayed through a hidden Docker socat container so the guest can
// reach the host-loopback DCP proxy. Opt-in because it requires Docker, which the rest of the
// sample deliberately does not. WEB_URL arrives in the guest as <gateway>:<relay port>; the
// literal BIND_DEMO is delivered exactly as written, untouched by the rewrite.
if (Setting("ConsumeWeb", "HCS_SAMPLE_CONSUME_WEB") is not null)
{
    worker
        .WithReference(web.GetEndpoint("http"))
        .WithEnvironment("BIND_DEMO", "127.0.0.1:9999");
}

// ---- Linux VM (opt-in) ----------------------------------------------------------------------
// Fixture: a Gen2/UEFI VHDX with a Linux OS installed, the hcsguest agent running (systemd),
// NIC on DHCP, and sshd enabled. Reference fixture: Rocky Linux 10, root only.
if (Setting("LinuxVhdx", "HCS_TEST_VHDX") is { } linuxVhdx)
{
    string linuxUser = Setting("LinuxUser", "HCS_TEST_VM_USER") ?? "root";

    var appliance = builder.AddHcsVm("appliance")
        .WithVhdx(linuxVhdx)
        .WithMemory(gigabytes: 2)
        .WithProcessorCount(2)
        .WithNetwork()
        .WithEndpoint("ssh", targetPort: 22)
        // Dashboard "Connect (SSH)" button. The account must exist in the image.
        .WithSshCommand(userName: linuxUser);

    appliance.WithHcsCtl(repoHcsCtl, storePath: store);

    // WaitFor as well as WithReference: the guest address exists only after the DHCP lease,
    // so the web app must not start (and capture its environment) before the VM is healthy.
    web.WithReference(appliance.GetEndpoint("ssh")).WaitFor(appliance);
}

// ---- Windows VM (opt-in) --------------------------------------------------------------------
// Fixture: a Gen2/UEFI VHDX with Windows installed, the hcsguest agent running as a service,
// NIC on DHCP, Remote Desktop enabled and its firewall group opened.
if (Setting("WindowsVhdx", "HCS_SAMPLE_WINDOWS_VHDX") is { } windowsVhdx)
{
    var winserver = builder.AddHcsVm("winserver")
        .WithVhdx(windowsVhdx)
        .WithMemory(gigabytes: 4)
        .WithProcessorCount(2)
        .WithNetwork()
        .WithEndpoint("rdp", targetPort: 3389)
        // Healthy once TermService accepts a connection on 3389.
        .WithTcpHealthCheck("rdp")
        // Dashboard "Connect (RDP)" button: opens mstsc with the leased address and this user.
        .WithRdpCommand(userName: Setting("WindowsUser", "HCS_SAMPLE_WINDOWS_USER") ?? "Administrator");

    winserver.WithHcsCtl(repoHcsCtl, storePath: store);

    web.WithReference(winserver.GetEndpoint("rdp")).WaitFor(winserver);
}

builder.Build().Run();
