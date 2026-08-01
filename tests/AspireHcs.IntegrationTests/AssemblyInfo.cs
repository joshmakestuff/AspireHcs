using Xunit;

// Most tests in this assembly boot a real VM on the host's single Default Switch; two at once
// contend for the same DHCP pool, host memory, and (in the assertion windows) the global HCN
// endpoint list. The scavenging race that once made concurrency actively incorrect is fixed —
// endpoints are pid-owned and scavenged only when their owning process is dead (#12) — but
// serial execution keeps the suite deterministic and the boot timings honest.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
