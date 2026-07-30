using Xunit;

// Every test in this assembly boots a real VM on the host's single Default Switch. Running two
// at once is not just heavy, it is incorrect: the orchestrator creates its HCN endpoint before
// its compute system exists, so a concurrently starting AppHost sees an endpoint with no live VM
// and scavenges it out from under the first — observed as two AppHosts deleting the same endpoint
// id in the same microsecond, leaving one VM with no NIC. That race is a product defect in its own
// right (concurrent AppHosts on one machine hit it too) and is tracked separately; serializing here
// keeps this suite from being flaky in the meantime.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
