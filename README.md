# AspireHcs

An experimental [Aspire](https://aspire.dev) hosting integration built on the Windows [Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview), starting with **Hyper-V virtual machines as Aspire resources**.

The goal is a normal "Aspire feel" for VMs in the local dev loop:

```csharp
var vm = builder.AddHcsVm("appliance")
    .WithVhdx(@"d:\images\appliance.vhdx", copyOnWrite: true)
    .WithMemory(gigabytes: 4)
    .WithProcessorCount(2)
    .WithNatNetwork()
    .WithEndpoint(name: "api", targetPort: 8080);

builder.AddProject<Projects.Web>("web")
    .WithReference(vm.GetEndpoint("api"))
    .WaitFor(vm);
```

## Design

- **HCS, not WMI**: HCS compute systems are ephemeral — created on `aspire run`, destroyed on exit — which matches Aspire's container-like dev-loop semantics. `ShouldTerminateOnLastHandleClosed` gives crash-safe teardown (no orphaned VMs after a killed debug session).
- **Custom resource, not `ExecutableResource`**: an `HcsVirtualMachineResource` (`IResource` + `IResourceWithEndpoints`) driven by Aspire's eventing pipeline (`InitializeResourceEvent` → `ResourceReadyEvent`), publishing state via `ResourceNotificationService` and streaming the VM serial console to the dashboard via `ResourceLoggerService`.
- **Networking via HCN**: NAT network + endpoint from the Host Compute Network API, surfaced as non-proxied Aspire endpoints.
- **Interop**: CsWin32-generated P/Invoke for `ComputeCore.dll` / `ComputeNetwork.dll`; HCS JSON schema (v2.x) config documents.

## Status / roadmap

1. [x] **Spike**: minimal console app that boots a VHDX via `HcsCreateComputeSystem`/`HcsStartComputeSystem`; verify whether Hyper-V Administrators membership suffices or full elevation is required. *(Result: it does — see [#1](https://github.com/joshmakestuff/AspireHcs/issues/1); elevation is not needed.)*
2. [ ] Internal `HcsClient` (schema POCOs + CsWin32 bindings).
3. [ ] Custom resource: lifecycle events, dashboard state, serial-console logs.
4. [ ] HCN NAT networking + endpoint publishing.
5. [ ] Polish: `WithReference` / connection strings, health checks, copy-on-write diff disks, `WithCommand` dashboard buttons.

## Requirements

- Windows 10 1809 / Windows Server 2019 or later, Hyper-V feature enabled.
- Windows-only by nature; the package fails fast on other platforms.
