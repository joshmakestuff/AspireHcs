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

**Both resource kinds require [hcsctl](https://github.com/joshmakestuff/hcsctl)**, which is not
bundled with this package. AspireHcs drives it for VMs and containers alike and resolves it from
an explicit path (`WithHcsCtl(path)`), the `ASPIREHCS_HCSCTL` environment variable, or `PATH`.
Container images must already be imported into an hcsctl store — a one-time step, and the import
needs elevation:

```
hcsctl image pull   --ref <ref> --store <dir>
hcsctl image import --ref <ref> --store <dir>   # elevated, once per image
```

Two more environment variables override defaults machine-wide: `ASPIREHCS_STORE` is the store
for any resource that does not name one with `WithStore(...)`/`WithHcsCtl(...)` (unset means
hcsctl's per-user store), and `ASPIREHCS_TEMP` relocates AspireHcs's temporary files — the
generated `.rdp` connection files — from `AspireHcs` under the system temp directory.

**Hyper-V isolation is the only container mode.** Process isolation needs an elevated token at
container create, which the unelevated dev loop does not have; AspireHcs refuses it at
model-build time. Hyper-V isolation runs fully unelevated.

A container has no guest-kernel readiness signal. Without `WithTcpHealthCheck()` it is declared
ready as soon as it reports Running, before anything inside it is listening.

Known gap: container logs carry hcsctl's whole stderr — guest output and hcsctl progress lines
interleaved ([hcsctl#28](https://github.com/joshmakestuff/hcsctl/issues/28)).

## Networking

`WithNetwork()` attaches a NIC on an existing host compute network. **Both resource kinds default
to the Hyper-V Default Switch.** Guests on one HNS network reach each other in both directions;
guests on different HNS networks cannot reach each other at all. A VM and a container that share
the default therefore talk to each other with no port publishing.

To isolate a resource, name another network: `WithNetwork("nat")` puts a container on the
network a Windows container host conventionally has, out of reach of Default Switch residents.

A resource that never calls `WithNetwork()` gets no NIC, and declaring endpoints on it is
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
loopback, which no HCS guest can reach. AspireHcs rewrites those values for the guest and stands
up the path they name: one hidden Docker relay container per AppHost session
(`aspirehcs-relay-<pid>-<suffix>`, `alpine/socat`) publishes one `0.0.0.0` host port per
referenced endpoint and forwards it to the endpoint's host port via `host.docker.internal`.
Where a host process would read `localhost:<port>`, the guest reads `<gateway>:<relay port>` —
the gateway being the default route of its own HNS network, read live from
`hcsctl network inspect`.

Only values that actually came from `WithReference` are rewritten. A literal set through
`WithEnvironment` — even one spelling `127.0.0.1:8080` — is configuration meant as written and
is delivered untouched. Referenced endpoints that do not point at the host's loopback (another
HCS guest on the same network, a real remote) also pass through, and need no Docker.

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

A VM reports **Running** once the `hcsguest` agent inside it answers and reports the address the
guest leased (`hcsctl vm ip`); the endpoints then resolve to that address.

Running is not the same as serving. Aspire declares a resource with no health checks ready the
moment it reports Running, so `WaitFor(vm)` releases dependents while the guest is still starting
its services. With `WithTcpHealthCheck()` the resource becomes healthy — and `WaitFor` releases —
only once a TCP connection to the endpoint is accepted. A refused connection is unhealthy; an
image whose daemon is disabled stays unhealthy until something listens.

## Dashboard commands

Start, Stop and Restart are available on the resource. Stop attempts a graceful guest shutdown
before terminating and releases the VM's HCN endpoint. Start boots a fresh differencing disk, so
a restart discards the previous run's writes.

`WithSshCommand()` and `WithRdpCommand()` add opt-in **Connect** buttons that open a client on the
host, already pointed at the address the guest leased:

```csharp
builder.AddHcsVm("appliance")
    .WithNetwork()
    .WithEndpoint(name: "ssh", targetPort: 22)
    .WithSshCommand(userName: "Administrator");
```

The client is launched on the host (run mode: the AppHost and the browser showing the dashboard
are on the same machine); the guest only has to serve SSH or RDP. Each button is disabled until
the VM is running *and* its endpoint has resolved. Pass the account the guest image has.

## Requirements

Both resource types:

- Windows 10 1809 / Windows Server 2019 or later with the Hyper-V feature enabled. The package throws `PlatformNotSupportedException` on other platforms.
- The AppHost process must run elevated **or** as a member of the **Hyper-V Administrators** group (joining the group takes effect after signing out and back in).
- `hcsctl` on `PATH`, in `ASPIREHCS_HCSCTL`, or given via `WithHcsCtl(path)`.

Virtual machines:

- A prepared, bootable Gen2/UEFI VHDX. AspireHcs boots a differencing child of it and never writes to the image. You supply the image; AspireHcs does not install operating systems or bootstrap guests. The sample's README lists the preparation steps for Linux and Windows guests.
- The guest image must run the `hcsguest` agent (from [hcsctl](https://github.com/joshmakestuff/hcsctl); in-guest installer scripts are under `install/` there). Readiness and the guest address come from the agent over a Hyper-V socket; a networked VM without it never reaches Running and fails start with a timeout naming the cause.
- The guest image must load the Hyper-V integration drivers (`hv_vmbus`, `hv_netvsc`, `hv_sock` on Linux; in-box on Windows).
- The guest image must configure its NIC for DHCP when using `WithNetwork()`; the agent reports the leased address — the default for stock Linux and Windows images. (Containers do **not** work this way: their address is assigned statically and known before the container starts.)
- Concurrent AppHosts on one host are supported: each run tags its HCN endpoints with its process id, and leftover endpoints from crashed runs are scavenged only once their owning process is gone. All VMs share the Default Switch's DHCP pool.

Containers:

- The image already imported into an hcsctl store (the import is elevated, once per image). A missing image fails resource start with the exact two commands to run.
- The host compute network named by `WithNetwork()` must already exist — the Default Switch by default, which every Windows client SKU with Hyper-V has. AspireHcs names a network; it does not create one.
- Concurrent AppHosts are supported: containers carry the owning process id in their id, and a leftover is reclaimed only once that process is gone.

## Where more details are

This package is pre-alpha. Work in progress is in the
[repository issues](https://github.com/joshmakestuff/AspireHcs/issues).
