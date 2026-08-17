using Xunit;

// Most tests in this assembly boot a real VM on the host's single Default Switch; two at once
// contend for the same DHCP pool, host memory, and (in the assertion windows) the global HCN
// endpoint list. Serial execution keeps the suite deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
