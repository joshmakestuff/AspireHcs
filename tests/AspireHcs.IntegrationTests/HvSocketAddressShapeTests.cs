using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace AspireHcs.IntegrationTests;

// Issue #5 investigation. The hvsocket probe returns WSAEINVAL (10022) against a booting VM from
// the first millisecond and never changes, which could mean the SOCKADDR_HV we build is malformed,
// or that the service GUID must be registered under
// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices.
//
// This discriminates the two without needing a VM: connect over HV_GUID_LOOPBACK to a service that
// IS registered in-box, and to one that is not. Different errors mean the address shape is fine and
// registration is the gate. Identical WSAEINVAL means our struct is wrong.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class HvSocketAddressShapeTests(ITestOutputHelper output)
{
    private static readonly Guid Loopback = new("e0e16197-dd56-4a10-9195-5ee7a155a838");

    // In-box, present in the registry on this host.
    private static readonly Guid VmSessionService = new("999E53D4-3D5C-4C3E-8779-BED06EC056E1");

    [SkippableFact]
    public async Task Registered_and_unregistered_service_ids_are_distinguishable()
    {
        Skip.If(Environment.GetEnvironmentVariable("ASPIREHCS_PROBE_EXPERIMENT") != "1",
            "Set ASPIREHCS_PROBE_EXPERIMENT=1 to run hvsocket investigation tests.");

        string registered = await HvSocketProbe.TryConnectRawAsync(Loopback, VmSessionService, TimeSpan.FromSeconds(2));
        string unregistered = await HvSocketProbe.TryConnectRawAsync(
            Loopback, HvSocketProbe.LinuxVSockServiceId(2761), TimeSpan.FromSeconds(2));

        output.WriteLine($"loopback + registered   (VM Session Service): {registered}");
        output.WriteLine($"loopback + unregistered (Linux VSOCK 2761)  : {unregistered}");
        output.WriteLine(registered == unregistered
            ? "SAME -> inconclusive; the address shape itself is suspect."
            : "DIFFERENT -> the address shape is accepted; registration is the gate.");
    }
}
