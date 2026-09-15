using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows10.0.17763")]

var builder = DistributedApplication.CreateBuilder(args);

// Hcs configuration takes precedence over the environment-variable fallbacks.
string? Setting(string key, string environmentVariable)
    => builder.Configuration[$"Hcs:{key}"] is { Length: > 0 } fromConfig
        ? fromConfig
        : Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : null;

// prepare.ps1 publishes GuestApi for the bind mount and imports the nanoserver image.
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

// Use the binary fetched by prepare.ps1 when neither ASPIREHCS_HCSCTL nor PATH supplies one.
string pinnedHcsCtl = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "tools", "hcsctl", "hcsctl.exe"));
bool ordinaryResolutionWorks =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPIREHCS_HCSCTL"))
    || (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(dir => dir.IndexOfAny(Path.GetInvalidPathChars()) < 0 && File.Exists(Path.Combine(dir, "hcsctl.exe")));
string? repoHcsCtl = !ordinaryResolutionWorks && File.Exists(pinnedHcsCtl) ? pinnedHcsCtl : null;

// This default must match the import destination in prepare.ps1.
string store = Setting("Store", "ASPIREHCS_STORE")
    ?? Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".store"));

var worker = builder.AddHcsContainer("worker")
    .WithImage(image)
    // Writable VSMB mounts make HCS refuse pause (0x80070032).
    // Read-only mounts still reflect host edits without restarting the container.
    .WithBindMount(guestApiPublish, @"C:\app", isReadOnly: true)
    .WithBindMount("data", @"C:\data", isReadOnly: true)
    .WithCommand(@"C:\app\HcsSample.GuestApi.exe")
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:8080")
    .WithEnvironment("DATA_DIR", @"C:\data")
    .WithEnvironment("GREETING", "Hello from a Hyper-V-isolated container")
    .WithNetwork()
    .WithEndpoint("http", targetPort: 8080, scheme: "http")
    .WithTcpHealthCheck()
    .WithStore(store)
    .WithShellCommand();

if (repoHcsCtl is not null)
{
    worker.WithHcsCtl(repoHcsCtl);
}

// No persistent volume: db/ seeds a fresh database on each AppHost run.
var db = builder.AddPostgres("pg")
    .WithBindMount("db", "/docker-entrypoint-initdb.d", isReadOnly: true)
    .AddDatabase("appdb");

var web = builder.AddProject<Projects.HcsSample_Web>("web")
    .WithReference(worker.GetEndpoint("http"))
    .WithReference(db)
    .WaitFor(worker)
    .WaitFor(db);

// WEB_URL uses a Docker relay to reach the host-loopback DCP proxy from the guest.
// BIND_DEMO is a literal and must not be rewritten.
bool consumeWeb = Setting("ConsumeWeb", "HCS_SAMPLE_CONSUME_WEB") is not null;
if (consumeWeb)
{
    worker
        .WithReference(web.GetEndpoint("http"))
        .WithEnvironment("BIND_DEMO", "127.0.0.1:9999");
}

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
        // The account must exist in the image.
        .WithSshCommand(userName: linuxUser);

    appliance.WithHcsCtl(repoHcsCtl, storePath: store);

    // WaitFor as well as WithReference: the guest address exists only after the DHCP lease,
    // so the web app must not start (and capture its environment) before the VM is healthy.
    web.WithReference(appliance.GetEndpoint("ssh")).WaitFor(appliance);

    // The VM as a consumer: the same references, delivered to /etc/aspire.env in the guest
    // over hvsocket. The web endpoint is a DCP proxy that listens from AppHost start, so the
    // VM can resolve it even though web itself waits for this VM.
    if (consumeWeb)
    {
        appliance
            .WithReference(web.GetEndpoint("http"))
            .WithEnvironment("BIND_DEMO", "127.0.0.1:9999");
    }
}

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
        .WithTcpHealthCheck("rdp")
        .WithRdpCommand(userName: Setting("WindowsUser", "HCS_SAMPLE_WINDOWS_USER") ?? "Administrator");

    winserver.WithHcsCtl(repoHcsCtl, storePath: store);

    web.WithReference(winserver.GetEndpoint("rdp")).WaitFor(winserver);
}

// Agentless appliances configure their own fixed address. WithGuestAddress waits for
// TCP on the first endpoint and skips agent-based address lookup and environment delivery.
if (Setting("ApplianceVhdx", "HCS_TEST_APPLIANCE_VHDX") is { } applianceVhdx
    && Setting("ApplianceAddress", "HCS_TEST_APPLIANCE_ADDRESS") is { } applianceAddress)
{
    var vendor = builder.AddHcsVm("vendor")
        .WithVhdx(applianceVhdx)
        .WithMemory(gigabytes: int.Parse(Setting("ApplianceMemoryGb", "HCS_TEST_APPLIANCE_MEMORY_GB") ?? "6"))
        .WithProcessorCount(int.Parse(Setting("ApplianceCpus", "HCS_TEST_APPLIANCE_CPUS") ?? "4"))
        .WithNetwork(Setting("ApplianceNetwork", "HCS_TEST_APPLIANCE_NETWORK") ?? "Default Switch")
        .WithGuestAddress(applianceAddress)
        .WithEndpoint("https", targetPort: 443, scheme: "https")
        .WithEndpoint("ssh", targetPort: 22)
        // The appliance's certificate is self-signed; the check proves the service answers.
        .WithInsecureHttpsHealthCheck("https",
            path: Setting("ApplianceHealthPath", "HCS_TEST_APPLIANCE_HEALTH_PATH") ?? "/")
        .WithSshCommand(userName: Setting("ApplianceSshUser", "HCS_TEST_APPLIANCE_SSH_USER") ?? "root");

    // Match the appliance's disk layout and any MAC/VLAN requirements in its network config.
    if (Setting("ApplianceDataVhdx", "HCS_TEST_APPLIANCE_DATA_VHDX") is { } applianceDataVhdx)
    {
        vendor.WithDisk(applianceDataVhdx);
    }
    if (Setting("ApplianceMac", "HCS_TEST_APPLIANCE_MAC") is { } applianceMac)
    {
        vendor.WithMacAddress(applianceMac);
    }
    if (Setting("ApplianceVlan", "HCS_TEST_APPLIANCE_VLAN") is { } applianceVlan)
    {
        vendor.WithVlan(int.Parse(applianceVlan));
    }

    vendor.WithHcsCtl(repoHcsCtl, storePath: store);
}

builder.Build().Run();
