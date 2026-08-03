# AspireHcs

An **experimental** [Aspire](https://aspire.dev) hosting integration that runs Hyper-V virtual machines as Aspire resources, built on the Windows [Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview).

VMs behave like containers in the local dev loop: created on `aspire run`, torn down on exit (crash-safe via `ShouldTerminateOnLastHandleClosed`), with state and logs in the Aspire dashboard.

```csharp
var vm = builder.AddHcsVm("appliance")
    .WithVhdx(@"d:\images\appliance.vhdx", copyOnWrite: true)
    .WithMemory(gigabytes: 4)
    .WithNatNetwork()
    .WithEndpoint(name: "api", targetPort: 8080)
    .WithTcpHealthCheck();

builder.AddProject<Projects.Api>("api").WithReference(vm).WaitFor(vm);
```

## Readiness

A VM reports **Running** once its guest kernel is up and its endpoints resolve to the address the
guest leased. On the reference image that is ~9 s (integration drivers) and ~14 s (DHCP) after
start.

Running is not the same as serving. Aspire declares a resource with no health checks ready the
moment it reports Running, so `WaitFor(vm)` would release dependents while the guest was still
starting its services. `WithTcpHealthCheck()` closes that gap: the resource becomes healthy — and
`WaitFor` releases — only once a TCP connection to the endpoint is accepted.

It is opt-in because it is strict on purpose. A refused connection is reported unhealthy, not
healthy, even though a refusal does prove the guest's network stack is up. Images that ship a
daemon disabled (Kali's `sshd`, for example) stay unhealthy until something actually listens.

## Dashboard commands

Start, Stop and Restart are available on the resource. Aspire wires those up only for resources
DCP owns, so AspireHcs registers its own: Stop attempts a graceful guest shutdown before
terminating and releases the VM's HCN endpoint, and Start boots a fresh differencing disk, so a
restart discards the previous run's writes the way a container restart does.

`WithSshCommand()` and `WithRdpCommand()` add opt-in **Connect** buttons that open a client on the
host, already pointed at the address the guest leased:

```csharp
builder.AddHcsVm("appliance")
    .WithNatNetwork()
    .WithEndpoint(name: "ssh", targetPort: 22)
    .WithSshCommand(userName: "Administrator");
```

They launch the client host-side, which is sound in run mode because the AppHost and the browser
showing the dashboard are on the same machine — so the guest cooperates with nothing beyond
serving SSH or RDP. Each button stays disabled until the VM is running *and* its endpoint has
resolved: a guest reaches Running before its DHCP lease surfaces, and connecting in that window
would fail rather than wait. They are opt-in because they start processes on the developer's
desktop, and because only the AppHost author knows which account the guest image actually has.

## Requirements

- Windows 10 1809 / Windows Server 2019 or later with the Hyper-V feature enabled. The package throws `PlatformNotSupportedException` on other platforms.
- The AppHost process must run elevated **or** as a member of the **Hyper-V Administrators** group (verified empirically; note that joining the group requires signing out and back in).
- The guest image must load the Hyper-V integration drivers (`hv_balloon` on Linux, in-box on Windows). The readiness probe resizes guest memory, which only the guest can satisfy; an image without them fails with a `TimeoutException` naming the cause rather than reporting a false ready.
- The guest image must configure its NIC for DHCP when using `WithNatNetwork()` — the default for stock Linux and Windows images.
- Concurrent AppHosts on one host are supported: each run tags its HCN endpoints with its process id, and leftover endpoints from crashed runs are scavenged only once their owning process is gone. Two caveats: builds predating this scheme tag endpoints without a pid and can still have a starting VM's NIC scavenged out from under them, so don't run old builds concurrently with anything; and all VMs share the Default Switch's DHCP pool.

## Status

Pre-alpha. The API above is the design target; see the [repository](https://github.com/joshmakestuff/AspireHcs) for the current roadmap.
