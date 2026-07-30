using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace AspireHcs.IntegrationTests;

/// <summary>
/// Minimal host-side Hyper-V socket connect, used to investigate whether hvsocket offers a
/// read-only guest-readiness signal (issue #5). Deliberately dependency-free and confined to
/// the test project: nothing here is a proposal for the library yet.
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal static class HvSocketProbe
{
    private const int AF_HYPERV = 34;
    private const int HV_PROTOCOL_RAW = 1;

    /// <summary>
    /// Linux guests do not use per-service GUIDs — they use AF_VSOCK, bridged to the host by this
    /// template whose first dword carries the VSOCK port.
    /// </summary>
    private static readonly Guid LinuxVSockTemplate = new("00000000-facb-11e6-bd58-64006a7986d3");

    internal static Guid LinuxVSockServiceId(uint port)
    {
        Span<byte> bytes = stackalloc byte[16];
        LinuxVSockTemplate.TryWriteBytes(bytes);
        // Data1 is the first dword in native (little-endian) GUID layout.
        BitConverter.TryWriteBytes(bytes, port);
        return new Guid(bytes);
    }

    /// <summary>
    /// Attempts a connection and reports what happened, so the caller can watch the failure mode
    /// change as the guest boots. The interesting question is whether "the guest's hvsocket
    /// transport is not up yet" is distinguishable from "the guest is up but nothing is listening".
    /// </summary>
    internal static Task<string> TryConnectAsync(Guid vmId, uint port, TimeSpan timeout)
        => TryConnectRawAsync(vmId, LinuxVSockServiceId(port), timeout);

    internal static async Task<string> TryConnectRawAsync(Guid vmId, Guid serviceId, TimeSpan timeout)
    {
        using Socket socket = new((AddressFamily)AF_HYPERV, SocketType.Stream, (ProtocolType)HV_PROTOCOL_RAW);
        try
        {
            await socket.ConnectAsync(new HyperVEndPoint(vmId, serviceId)).WaitAsync(timeout);
            return "connected";
        }
        catch (SocketException ex)
        {
            return $"{ex.SocketErrorCode} (native {ex.NativeErrorCode})";
        }
        catch (TimeoutException)
        {
            return "timeout";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// SOCKADDR_HV: family (2) + reserved (2) + VmId (16) + ServiceId (16).
    /// </summary>
    private sealed class HyperVEndPoint(Guid vmId, Guid serviceId) : EndPoint
    {
        public override AddressFamily AddressFamily => (AddressFamily)AF_HYPERV;

        public override SocketAddress Serialize()
        {
            SocketAddress address = new((AddressFamily)AF_HYPERV, 36);
            Span<byte> buffer = stackalloc byte[16];

            vmId.TryWriteBytes(buffer);
            for (int i = 0; i < 16; i++)
            {
                address[4 + i] = buffer[i];
            }

            serviceId.TryWriteBytes(buffer);
            for (int i = 0; i < 16; i++)
            {
                address[20 + i] = buffer[i];
            }

            return address;
        }

        public override EndPoint Create(SocketAddress socketAddress) => this;
    }
}
