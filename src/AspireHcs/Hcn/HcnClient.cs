using System.Text.Json.Nodes;
using AspireHcs.Hcs;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AspireHcs.Hcn;

/// <summary>
/// Thin wrapper over the Host Compute Network API (ComputeNetwork.dll). Used to attach VM NICs
/// to the host's ICS network (the "Default Switch"), whose built-in DHCP/NAT/DNS serves
/// arbitrary guest images — HNS static-IP endpoints only program *container* guests, so a full
/// VM must lease its address like any physical machine would.
/// </summary>
internal static unsafe class HcnClient
{
    /// <summary>
    /// Finds the Hyper-V "Default Switch" network — the ICS-mode network whose DHCP/NAT/DNS
    /// serve arbitrary guests. Matched by name because hosts can have several ICS-mode
    /// networks (e.g. WSL's firewalled one, which must not be picked up); any other ICS
    /// network is only a fallback. Present on Windows client SKUs with Hyper-V enabled.
    /// </summary>
    public static Guid FindIcsNetworkId()
    {
        HRESULT hr = PInvoke.HcnEnumerateNetworks("{}", out PWSTR networksDoc, out PWSTR error);
        string networksJson = Consume("HcnEnumerateNetworks", hr, networksDoc, error) ?? "[]";

        Guid? icsFallback = null;
        JsonArray ids = JsonNode.Parse(networksJson) as JsonArray ?? [];
        foreach (JsonNode? idNode in ids)
        {
            if (!Guid.TryParse(idNode?.GetValue<string>(), out Guid id))
            {
                continue;
            }

            string? properties = QueryNetworkProperties(id);
            JsonNode? parsed = properties is null ? null : JsonNode.Parse(properties);
            if (!string.Equals(parsed?["Type"]?.GetValue<string>(), "ICS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(parsed?["Name"]?.GetValue<string>(), "Default Switch", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
            icsFallback ??= id;
        }

        return icsFallback ?? throw new InvalidOperationException(
            "No ICS (Default Switch) host compute network was found. AspireHcs VM networking relies on the " +
            "Hyper-V Default Switch for DHCP/NAT; it exists on Windows client SKUs with Hyper-V enabled.");
    }

    /// <summary>
    /// The IPv4 address HNS reserved for the endpoint. On an ICS network the DHCP server
    /// leases exactly this address to the endpoint's MAC, so it is known deterministically
    /// before the guest even boots (verified empirically).
    /// </summary>
    public static string GetEndpointAssignedIp(Guid endpointId)
    {
        string? properties = QueryEndpointProperties(endpointId);
        string? ip = properties is null ? null : JsonNode.Parse(properties)?["IPAddress"]?.GetValue<string>();
        return ip ?? throw new InvalidOperationException(
            $"HCN endpoint {endpointId} has no assigned IPAddress in its properties.");
    }

    public static string? QueryNetworkProperties(Guid networkId)
    {
        void* network = OpenNetwork(networkId);
        try
        {
            HRESULT hr = PInvoke.HcnQueryNetworkProperties(network, "{}", out PWSTR properties, out PWSTR error);
            return Consume("HcnQueryNetworkProperties", hr, properties, error);
        }
        finally
        {
            PInvoke.HcnCloseNetwork(network);
        }
    }

    /// <summary>
    /// Creates a DHCP-mode endpoint (vNIC port) on the given network with a caller-chosen MAC.
    /// The endpoint outlives the compute system — callers own deletion.
    /// </summary>
    public static void CreateDhcpEndpoint(Guid networkId, Guid endpointId, string macAddress, string owner)
    {
        // Flags is EndpointFlags.EnableDhcp (bit 32). The HCN schema docs say enum variants
        // "should be used as string", but HNS empirically rejects string flags with
        // 0x803B001B InvalidJson — only the numeric form is accepted.
        string settings = $$"""
            {
                "SchemaVersion": { "Major": 2, "Minor": 0 },
                "Owner": "{{owner}}",
                "HostComputeNetwork": "{{networkId}}",
                "MacAddress": "{{macAddress}}",
                "Flags": 32
            }
            """;

        void* network = OpenNetwork(networkId);
        try
        {
            HRESULT hr = PInvoke.HcnCreateEndpoint(network, endpointId, settings, out void* endpoint, out PWSTR error);
            Consume("HcnCreateEndpoint", hr, default, error);
            PInvoke.HcnCloseEndpoint(endpoint);
        }
        finally
        {
            PInvoke.HcnCloseNetwork(network);
        }
    }

    public static string? QueryEndpointProperties(Guid endpointId)
    {
        HRESULT hr = PInvoke.HcnOpenEndpoint(endpointId, out void* endpoint, out PWSTR openError);
        Consume("HcnOpenEndpoint", hr, default, openError);
        try
        {
            hr = PInvoke.HcnQueryEndpointProperties(endpoint, "{}", out PWSTR properties, out PWSTR error);
            return Consume("HcnQueryEndpointProperties", hr, properties, error);
        }
        finally
        {
            PInvoke.HcnCloseEndpoint(endpoint);
        }
    }

    public static void DeleteEndpoint(Guid endpointId)
    {
        HRESULT hr = PInvoke.HcnDeleteEndpoint(endpointId, out PWSTR error);
        Consume("HcnDeleteEndpoint", hr, default, error);
    }

    /// <summary>Returns endpoint ids owned by <paramref name="owner"/> (for scavenging stale runs).</summary>
    public static List<Guid> EnumerateEndpointIds(string owner)
    {
        string query = $$"""{ "Owner": "{{owner}}" }""";
        HRESULT hr = PInvoke.HcnEnumerateEndpoints(query, out PWSTR endpointsDoc, out PWSTR error);
        string json = Consume("HcnEnumerateEndpoints", hr, endpointsDoc, error) ?? "[]";

        List<Guid> ids = [];
        if (JsonNode.Parse(json) is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (Guid.TryParse(node?.GetValue<string>(), out Guid id))
                {
                    ids.Add(id);
                }
            }
        }
        return ids;
    }

    private static void* OpenNetwork(Guid networkId)
    {
        HRESULT hr = PInvoke.HcnOpenNetwork(networkId, out void* network, out PWSTR error);
        Consume("HcnOpenNetwork", hr, default, error);
        return network;
    }

    /// <summary>Frees the CoTaskMem strings an HCN call returns; throws with the error record folded in on failure.</summary>
    private static string? Consume(string step, HRESULT hr, PWSTR document, PWSTR errorRecord)
    {
        string? doc = ReadAndFree(document);
        string? error = ReadAndFree(errorRecord);
        return hr.Failed ? throw HcsException.Create(step, hr, error) : doc;

        static string? ReadAndFree(PWSTR value)
        {
            if (value.Value == null)
            {
                return null;
            }
            string text = value.ToString();
            PInvoke.CoTaskMemFree(value.Value);
            return text;
        }
    }
}
