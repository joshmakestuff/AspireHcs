# AspireHcs

An **experimental** [Aspire](https://aspire.dev) hosting integration that runs Hyper-V virtual machines as Aspire resources, built on the Windows [Host Compute System (HCS) API](https://learn.microsoft.com/virtualization/api/hcs/overview).

VMs behave like containers in the local dev loop: created on `aspire run`, torn down on exit (crash-safe via `ShouldTerminateOnLastHandleClosed`), with state and logs in the Aspire dashboard.

```csharp
var vm = builder.AddHcsVm("appliance")
    .WithVhdx(@"d:\images\appliance.vhdx", copyOnWrite: true)
    .WithMemory(gigabytes: 4)
    .WithNatNetwork()
    .WithEndpoint(name: "api", targetPort: 8080);
```

## Requirements

- Windows 10 1809 / Windows Server 2019 or later with the Hyper-V feature enabled. The package throws `PlatformNotSupportedException` on other platforms.
- The AppHost process must run elevated **or** as a member of the **Hyper-V Administrators** group (verified empirically; note that joining the group requires signing out and back in).

## Status

Pre-alpha. The API above is the design target; see the [repository](https://github.com/joshmakestuff/AspireHcs) for the current roadmap.
