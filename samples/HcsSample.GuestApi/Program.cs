// Runs inside the container: /info reports guest details and /files reads the VSMB data mount.

using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string dataDirectory = app.Configuration["DATA_DIR"] ?? @"C:\data";
DateTimeOffset started = DateTimeOffset.UtcNow;

app.MapGet("/", () => Results.Redirect("/info"));

app.MapGet("/info", () => new
{
    machine = Environment.MachineName,
    os = Environment.OSVersion.VersionString,
    processPath = Environment.ProcessPath,
    uptimeSeconds = (long)(DateTimeOffset.UtcNow - started).TotalSeconds,
    // Delivered by WithEnvironment in the AppHost; proves environment reaches the guest.
    greeting = app.Configuration["GREETING"] ?? "(GREETING was not set)",
});

// WEB_URL points through the relay to the web project. BIND_DEMO remains a literal.
// /consume reports both values and fetches WEB_URL from inside the guest.
app.MapGet("/consume", async () =>
{
    string? referenced = app.Configuration["services:web:http:0"];
    string? bindDemo = app.Configuration["BIND_DEMO"];

    string fetch;
    if (referenced is null)
    {
        fetch = "(no reference injected; run the AppHost with HCS_SAMPLE_CONSUME_WEB=1)";
    }
    else
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(referenced);
            fetch = $"GET {referenced} -> {(int)response.StatusCode} {response.StatusCode}";
        }
        catch (Exception ex)
        {
            fetch = $"GET {referenced} failed: {ex.GetBaseException().Message}";
        }
    }

    return Results.Ok(new
    {
        referencedWebUrl = referenced,
        bindDemo,
        fetch,
    });
});

app.MapGet("/files", () =>
{
    if (!Directory.Exists(dataDirectory))
    {
        return Results.Ok(Array.Empty<object>());
    }

    var files = Directory.EnumerateFiles(dataDirectory)
        .Select(path => new FileInfo(path))
        .Select(file => new
        {
            name = file.Name,
            bytes = file.Length,
            // Text content only, and only when small: this is a demo listing, not a file server.
            text = file.Extension is ".txt" or ".md" && file.Length <= 16 * 1024
                ? File.ReadAllText(file.FullName)
                : null,
        });

    return Results.Ok(files);
});

app.Run();
