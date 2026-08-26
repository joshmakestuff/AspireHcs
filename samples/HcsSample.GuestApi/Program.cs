// A deliberately small API that runs INSIDE the Hyper-V-isolated container. It exists to prove
// where it runs: /info reports the guest's own machine name and OS, and /files lists the
// bind-mounted data directory, live from the host over VSMB.

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
