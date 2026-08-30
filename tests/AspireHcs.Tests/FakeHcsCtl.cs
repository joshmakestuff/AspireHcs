#:property TargetFramework=net10.0
#:property PublishAot=false

using System.Text;
using System.Text.Json;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
{
    AutoFlush = true,
});

string scenarioPath = Path.Combine(AppContext.BaseDirectory, "scenario.json");
FakeHcsCtlScenario scenario = JsonSerializer.Deserialize<FakeHcsCtlScenario>(File.ReadAllText(scenarioPath))
    ?? throw new InvalidOperationException($"The fake hcsctl scenario at '{scenarioPath}' was null.");

if (scenario.ArgumentsPath is { Length: > 0 } argumentsPath)
{
    File.WriteAllText(argumentsPath, JsonSerializer.Serialize(args), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

FakeHcsCtlResponse response = scenario.Responses.FirstOrDefault(r => Matches(args, r.ArgumentPrefix))
    ?? scenario.DefaultResponse
    ?? throw new InvalidOperationException("The fake hcsctl scenario had no matching response.");

Console.Out.Write(response.Stdout);
Console.Out.Flush();
Console.Error.Write(response.Stderr);
Console.Error.Flush();

if (response.ReadyPath is { Length: > 0 } readyPath)
{
    File.WriteAllText(readyPath, "ready");
}

if (response.ReleasePath is { Length: > 0 } releasePath)
{
    while (!File.Exists(releasePath))
    {
        Thread.Sleep(TimeSpan.FromMilliseconds(10));
    }
}

return response.ExitCode;

static bool Matches(IReadOnlyList<string> arguments, IReadOnlyList<string>? prefix)
{
    if (prefix is null || prefix.Count > arguments.Count)
    {
        return false;
    }

    for (int i = 0; i < prefix.Count; i++)
    {
        if (!string.Equals(arguments[i], prefix[i], StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

internal sealed record FakeHcsCtlScenario
{
    public string? ArgumentsPath { get; init; }

    public IReadOnlyList<FakeHcsCtlResponse> Responses { get; init; } = [];

    public FakeHcsCtlResponse? DefaultResponse { get; init; }
}

internal sealed record FakeHcsCtlResponse
{
    public IReadOnlyList<string>? ArgumentPrefix { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";

    public int ExitCode { get; init; }

    public string? ReadyPath { get; init; }

    public string? ReleasePath { get; init; }
}
