// Probe behind the "ArgumentList survives ShellExecuteEx" claim in docs/connect-ux.md.
//
// ConnectCommands.BuildSshStartInfo passes arguments through ProcessStartInfo.ArgumentList
// with UseShellExecute = true and does NOT escape argv itself. Two things had to be true for
// that to be safe, and both were assumed until this was run:
//
//   1. ArgumentList is accepted alongside UseShellExecute = true at all.
//   2. The framework's quoting SURVIVES the ShellExecuteEx path — a user name containing a
//      space or a backslash arrives at the child unchanged.
//
// Method: the process re-launches ITSELF, so no intermediate parser (no cmd.exe, no
// PowerShell) can be blamed for a difference.
//
// Run it:  dotnet run docs/probes/argv-roundtrip.cs
// Exit 0 means every argument round-tripped identically.
//
// This is not a unit test because asserting it needs a purpose-built child executable, and the
// test projects have no such binary to launch. Re-run it by hand if the launch path changes.

using System.Diagnostics;

string outFile = Path.Combine(Path.GetTempPath(), "aspirehcs-argv-probe.txt");
string[] tricky =
[
    "plain",
    "has a space",
    "has\"quote",
    @"trailing\slash\",
    "amp&caret^pipe|",
    @"Domain\User Name",   // the realistic RDP/SSH user name case
];

if (args.Length > 0)
{
    File.WriteAllLines(outFile, args);
    return 0;
}

// Printed because "does ProcessPath point at the probe or at dotnet.exe?" is the first thing
// anyone will doubt about this method. A file-based app is compiled to a real apphost, so this
// is the probe's own executable — shown rather than asserted in prose.
Console.WriteLine($"relaunching: {Environment.ProcessPath}");

File.Delete(outFile);
ProcessStartInfo psi = new(Environment.ProcessPath!)
{
    UseShellExecute = true,
    WindowStyle = ProcessWindowStyle.Hidden,
};
foreach (string t in tricky)
{
    psi.ArgumentList.Add(t);
}

using Process child = Process.Start(psi)!;
child.WaitForExit();

if (!File.Exists(outFile))
{
    Console.WriteLine("FAIL: the child wrote nothing.");
    return 1;
}

string[] got = File.ReadAllLines(outFile);
File.Delete(outFile);

bool ok = got.SequenceEqual(tricky);
Console.WriteLine($"sent {tricky.Length} args, got {got.Length}: {(ok ? "IDENTICAL" : "MISMATCH")}");
for (int i = 0; i < Math.Max(got.Length, tricky.Length); i++)
{
    string sent = i < tricky.Length ? tricky[i] : "<none>";
    string recv = i < got.Length ? got[i] : "<none>";
    Console.WriteLine($"  [{i}] {(sent == recv ? "ok  " : "DIFF")} sent={sent,-22} recv={recv}");
}

return ok ? 0 : 1;
