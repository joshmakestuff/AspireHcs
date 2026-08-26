// The host-side frontend. It consumes the HCS guests the way any Aspire project consumes a
// reference: the AppHost injects each referenced endpoint as service-discovery configuration
// (services:<resource>:<endpoint>:0), and this app calls straight into the guest's address.
// The browser talks only to this app; the guest calls happen server-side.

using System.Net.Sockets;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("worker", client => client.Timeout = TimeSpan.FromSeconds(2));
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

string? WorkerBase() => app.Configuration["services:worker:http:0"];

// Proxies /api/worker/info and /api/worker/files into the container. A timeout is reported as
// unreachable rather than an error: a paused container answers nothing, and showing that state
// is part of the demo.
app.MapGet("/api/worker/{endpoint}", async (string endpoint, IHttpClientFactory factory, CancellationToken ct) =>
{
    if (endpoint is not ("info" or "files"))
    {
        return Results.NotFound();
    }

    if (WorkerBase() is not { Length: > 0 } baseAddress)
    {
        return Results.Json(new { reachable = false, reason = "The worker container is not configured." });
    }

    try
    {
        HttpClient client = factory.CreateClient("worker");
        using HttpResponseMessage response = await client.GetAsync(new Uri(new Uri(baseAddress), endpoint), ct);
        response.EnsureSuccessStatusCode();
        using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Results.Json(new { reachable = true, address = baseAddress, payload = payload.RootElement.Clone() });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
    {
        return Results.Json(new { reachable = false, address = baseAddress, reason = ex.GetType().Name });
    }
});

// TCP-probes every referenced VM endpoint (ssh/rdp). A VM guest runs no HTTP service for this
// demo; accepting a TCP connection is the proof it is up and reachable.
app.MapGet("/api/vms", async (CancellationToken ct) =>
{
    var probes = new List<object>();
    foreach ((string resource, string endpoint) in new[] { ("appliance", "ssh"), ("winserver", "rdp") })
    {
        if (app.Configuration[$"services:{resource}:{endpoint}:0"] is not { Length: > 0 } value
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            continue;
        }

        bool reachable;
        try
        {
            using TcpClient tcp = new();
            await tcp.ConnectAsync(uri.Host, uri.Port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct);
            reachable = true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            reachable = false;
        }

        probes.Add(new { resource, endpoint, address = $"{uri.Host}:{uri.Port}", reachable });
    }

    return Results.Ok(probes);
});

app.Run();
