# AspireHcs

An **experimental** [Aspire](https://aspire.dev) hosting integration for the Windows [Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview): **Hyper-V virtual machines** and **Hyper-V-isolated Windows containers** as Aspire resources.

Both are ephemeral in the local dev loop: created on `aspire run`, torn down on exit, with state and logs in the Aspire dashboard.

## Virtual machines

```csharp
var vm = builder.AddHcsVm("appliance")
    .WithVhdx(@"d:\images\appliance.vhdx")
    .WithMemory(gigabytes: 4)
    .WithNetwork()
    .WithEndpoint(name: "api", targetPort: 8080)
    .WithTcpHealthCheck();

builder.AddProject<Projects.Api>("api").WithReference(vm).WaitFor(vm);
```

## Windows containers

```csharp
var worker = builder.AddHcsContainer("worker")
    .WithImage("mcr.microsoft.com/windows/servercore:ltsc2022")
    .WithCommand(@"C:\app\worker.exe")
    .WithStore(@"E:\hcsctl-store")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithBindMount(@".\data", @"C:\data")
    .WithNetwork()
    .WithEndpoint(name: "http", targetPort: 8080)
    .WithTcpHealthCheck();
```

**Containers additionally require [hcsctl](https://github.com/joshmakestuff/hcsctl)**, which is not
bundled with this package. AspireHcs drives it rather than calling HCS directly for containers, and
resolves it from an explicit path, the `ASPIREHCS_HCSCTL` environment variable, or `PATH`. Images
must already be imported into an hcsctl store — a one-time step, and the import needs elevation:

```
hcsctl image pull   --ref <ref> --store <dir>
hcsctl image import --ref <ref> --store <dir>   # elevated, once per image
```

**Hyper-V isolation is the only container mode**, permanently. Process isolation requires an
enabled `BUILTIN\Administrators` SID at `PrepareLayer`, which runs at *every* container start — a
group check no user-rights assignment satisfies in a UAC-filtered token. It is refused up front
rather than attempted.

A container has no separate guest-kernel readiness signal, so `WithTcpHealthCheck()` is the **only**
readiness gate: without it a container is declared ready the moment it reports Running, which is
before anything inside it is listening.

Known gap: container logs currently carry hcsctl's whole stderr — the guest's output and hcsctl's
own progress lines interleaved — because the two are not separable yet
([hcsctl#28](https://github.com/joshmakestuff/hcsctl/issues/28)).

## Networking

`WithNetwork()` attaches a NIC on an existing host compute network, and **both resource kinds
default to the same one: the Hyper-V Default Switch**. That is deliberate. Guests on one HNS
network reach each other in both directions; guests on different HNS networks are isolated —
every probe dropped (measured). Sharing the default is what lets a VM and a Hyper-V-isolated
container in one AppHost talk to each other out of the box, with no port publishing involved.

Cross-network isolation is the opt-in, not the other way around: name a network to place a
resource where the Default Switch residents cannot reach it — `WithNetwork("nat")` puts a
container on the network a Windows container host conventionally has, alone.

A resource that never calls `WithNetwork()` gets no NIC at all, and declaring endpoints on it is
refused at start.

## Consuming other resources: `WithReference`

HCS resources are consumers, not only servers:

```csharp
var cache = builder.AddRedis("cache");

var worker = builder.AddHcsContainer("worker")
    .WithImage(...)
    .WithNetwork()
    .WithReference(cache);   // ConnectionStrings__cache arrives inside the guest, reachable
```

Stock Aspire resolves reference values from the host's perspective — endpoints on the host's
loopback, which no HCS guest can reach (measured: every guest→host-loopback probe drops).
AspireHcs rewrites those values for the guest and stands up the path they name: one hidden
Docker relay container per AppHost session (`aspirehcs-relay-<pid>`, `alpine/socat`) publishes
one `0.0.0.0` host port per referenced endpoint and forwards it to the endpoint's host port via
`host.docker.internal`. Where a host process would read `localhost:<port>`, the guest reads
`<gateway>:<relay port>` — the gateway being the `.1` of its own HNS network, derived live from
`hcsctl network ls`. Values that do not point at the host's loopback (another HCS guest on the
same network, a real remote) pass through untouched, and need no Docker.

**This needs Docker** (Docker Desktop, or any engine with a docker-compatible CLI on `PATH`) —
already an Aspire prerequisite in practice. An AppHost whose HCS resources reference nothing on
the host's loopback never touches it. Relay containers left by crashed runs are scavenged by the
same pid discipline as HCS containers.

Delivery differs by resource kind:

- **Containers** get the values as process environment, at exec.
- **VMs** have no create-time injection — nothing writes variables into a VHDX — so once the
  guest is up, the values are written to **`/etc/aspire.env`** in the guest over hvsocket
  (requires the `hcsguest` agent in the image; the convention is for Linux guests today). The
  caveat that comes with it: a workload that autostarts at boot may run before the file lands; a
  workload that reads the file when it starts is correct. Tighter ordering is future work.

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
    .WithNetwork()
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

Both resource types:

- Windows 10 1809 / Windows Server 2019 or later with the Hyper-V feature enabled. The package throws `PlatformNotSupportedException` on other platforms.
- The AppHost process must run elevated **or** as a member of the **Hyper-V Administrators** group (verified empirically; note that joining the group requires signing out and back in).

Virtual machines:

- The guest image must load the Hyper-V integration drivers (`hv_balloon` on Linux, in-box on Windows). The readiness probe resizes guest memory, which only the guest can satisfy; an image without them fails with a `TimeoutException` naming the cause rather than reporting a false ready.
- The guest image must configure its NIC for DHCP when using `WithNetwork()`, and AspireHcs discovers the leased address afterwards — the default for stock Linux and Windows images. (Containers do **not** work this way: their address is assigned statically and known before the container starts.)
- Concurrent AppHosts on one host are supported: each run tags its HCN endpoints with its process id, and leftover endpoints from crashed runs are scavenged only once their owning process is gone. Two caveats: builds predating this scheme tag endpoints without a pid and can still have a starting VM's NIC scavenged out from under them, so don't run old builds concurrently with anything; and all VMs share the Default Switch's DHCP pool.

Containers:

- `hcsctl` on `PATH`, in `ASPIREHCS_HCSCTL`, or given via `WithHcsCtl(path)`.
- The image already imported into an hcsctl store (the import is elevated, once per image). A missing image fails resource start with the exact two commands to run.
- The host compute network named by `WithNetwork()` must already exist — the Default Switch by default, which every Windows client SKU with Hyper-V has. AspireHcs names a network; it does not create one.
- Concurrent AppHosts are supported here too, and by the same discipline: containers carry the owning process id in their id, and a leftover is reclaimed only once that process is gone.

## Where more details are

This package is pre-alpha, and the API above is the design target. The roadmap and work in
progress are in the [repository issues](https://github.com/joshmakestuff/AspireHcs/issues).
