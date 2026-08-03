// `identify` (issue #30 / #33 follow-up): name the privilege that gates layer
// finalization, so "a required privilege is not held" becomes a specific,
// grantable thing rather than a synonym for "run as admin".
//
// Why it matters: SeBackupPrivilege/SeRestorePrivilege are granted by the
// Backup Operators group, so IF they are the gate, a token that merely holds
// them — no elevation — may be enough, and acquisition could become a one-time
// group grant like the Hyper-V Administrators prerequisite the VM path already
// documents. That is a HYPOTHESIS this command informs, not one it proves: the
// only way to establish it is to put an account in that group and run `import`
// unelevated. See the verdict text, which is careful about the difference.
//
// Method: four arms over four IDENTICAL freshly-extracted trees, differing only
// in which privileges are ENABLED. Four entries because ProcessBaseImage is not
// idempotent — an arm run against another arm's leftovers measures the
// leftovers (0x80070050), not the privilege.
//
// Requires elevation, because turning a privilege OFF is only meaningful in a
// token that holds it.

using Windows.Win32.Foundation;

namespace HcsContainerSpike;

internal static partial class Program
{
    private const int PrivilegeNotHeld = unchecked((int)0x80070522);

    /// <summary>Arm results live in their own record rather than going through
    /// <c>Step</c>. An arm that FAILS is the expected, informative outcome here,
    /// and Main promotes any failed Results row to exit 4 — so recording arms
    /// there would make a successful identification report itself as a failed
    /// run.</summary>
    private static readonly List<(string Arm, HRESULT Hr, string Detail)> Arms = [];

    private static int Identify(string[] args)
    {
        string raw = Opt(args, "--entries")
            ?? throw new ArgumentException("--entries <dir1,dir2,dir3,dir4> is required (four pristine extracted entries)");
        string[] entries = [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e)))];
        if (entries.Length != 4 || entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            {
            throw new ArgumentException($"--entries needs FOUR distinct directories, got {entries.Length}");
        }

        Console.WriteLine();
        Console.WriteLine("--- Token privileges ---");
        IReadOnlyList<(string Name, bool Enabled)>? privileges = TokenPrivileges.ListProcessPrivileges();
        if (privileges is null)
        {
            // Unreadable is not empty. Treating it as "holds nothing" would turn
            // a failure to look into a finding about the token.
            Step("IdentifyPreconditions", ProbeFailed, "the process token could not be read — no claim can be made about its privileges");
            return 2;
        }
        foreach ((string name, bool enabled) in privileges)
        {
            Console.WriteLine($"  {(enabled ? "enabled " : "disabled")}  {name}");
        }
        bool holdsBackup = privileges.Any(p => p.Name == TokenPrivileges.SeBackupPrivilege);
        bool holdsRestore = privileges.Any(p => p.Name == TokenPrivileges.SeRestorePrivilege);
        Console.WriteLine($"  holds SeBackupPrivilege={holdsBackup} SeRestorePrivilege={holdsRestore}");
        if (!holdsBackup || !holdsRestore)
        {
            Step("IdentifyPreconditions", ProbeFailed,
                "this token does not HOLD SeBackup/SeRestore, so the arms cannot differ and would all fail " +
                "identically. Run elevated (or as a member of Backup Operators).");
            return 2;
        }

        // Every entry must be pristine, or its arm measures leftovers instead of
        // privilege. ProcessBaseImage fails 0x80070050 against its own earlier
        // output, which looks nothing like a privilege result.
        foreach (string entry in entries)
        {
            HRESULT probe = ProbeEntryFile(entry, ScratchTemplateNames, out string detail);
            if (probe.Value != ProbeFailed.Value)
            {
                Step("IdentifyPreconditions", ProbeFailed,
                    $"{entry} already carries finalize output ({detail}) — re-extract it; ProcessBaseImage is not " +
                    "idempotent and this arm would measure the leftovers");
                return 2;
            }
            if (!Directory.Exists(Path.Combine(entry, "Files")))
            {
                Step("IdentifyPreconditions", ProbeFailed, $"{entry} has no Files\\ — not an extracted layer");
                return 2;
            }
        }

        (string Label, string[] Enable)[] plan =
        [
            ("neither", []),
            ("SeBackupPrivilege only", [TokenPrivileges.SeBackupPrivilege]),
            ("SeRestorePrivilege only", [TokenPrivileges.SeRestorePrivilege]),
            ("both", [TokenPrivileges.SeBackupPrivilege, TokenPrivileges.SeRestorePrivilege]),
        ];

        for (int i = 0; i < plan.Length; i++)
        {
            (string label, string[] enable) = plan[i];

            // Start from a known state every time: disable both, then enable
            // exactly this arm's set. A failure to establish that state INVALIDATES
            // the arm — running the call anyway would attribute a result to a
            // privilege configuration that was never actually in force.
            HRESULT disableHr = TokenPrivileges.DisableProcessPrivileges(
                [TokenPrivileges.SeBackupPrivilege, TokenPrivileges.SeRestorePrivilege], out string disableDetail);
            if (disableHr.Failed)
            {
                Step("IdentifyArmSetup", disableHr, $"arm '{label}': could not disable privileges ({disableDetail}) — experiment invalid");
                return 2;
            }
            if (enable.Length > 0)
            {
                HRESULT enableHr = TokenPrivileges.EnableProcessPrivileges(enable, out string enableDetail);
                if (enableHr.Failed)
                {
                    Step("IdentifyArmSetup", enableHr, $"arm '{label}': could not enable {string.Join('+', enable)} ({enableDetail}) — experiment invalid");
                    return 2;
                }
            }

            // Witness the state actually in force, rather than assuming the
            // adjust calls did what they reported.
            IReadOnlyList<(string Name, bool Enabled)>? now = TokenPrivileges.ListProcessPrivileges();
            string witnessed = now is null
                ? "(token unreadable)"
                : $"backup={now.Any(p => p.Name == TokenPrivileges.SeBackupPrivilege && p.Enabled)} " +
                  $"restore={now.Any(p => p.Name == TokenPrivileges.SeRestorePrivilege && p.Enabled)}";

            HRESULT result = WcLayer.ProcessBase(entries[i]);
            Arms.Add((label, result, $"enabled: {witnessed}; entry={entries[i]}"));
            Console.WriteLine($"[arm] {label,-24} hr=0x{(uint)result.Value:X8}  {witnessed}");
        }

        PrintArmVerdict();
        // Exit reflects whether the EXPERIMENT was valid, not whether the arms
        // passed: a failing arm is the datum this command exists to collect.
        return 0;
    }

    private static void PrintArmVerdict()
    {
        Console.WriteLine();
        Console.WriteLine("=== Arms ===");
        foreach ((string arm, HRESULT hr, string detail) in Arms)
        {
            Console.WriteLine($"  {(hr.Succeeded ? "OK  " : "FAIL")}  0x{(uint)hr.Value:X8}  {arm,-24}  {Truncate(detail)}");
        }

        HRESULT neither = Arms[0].Hr, backupOnly = Arms[1].Hr, restoreOnly = Arms[2].Hr, both = Arms[3].Hr;
        Console.WriteLine();
        Console.WriteLine("=== Verdict ===");

        if (neither.Succeeded)
        {
            Console.WriteLine("NOT a SeBackup/SeRestore gate: the call succeeded with BOTH disabled, so elevation");
            Console.WriteLine("supplies something else — another privilege, or an access check. Bisect the remaining");
            Console.WriteLine("enabled privileges listed above.");
            return;
        }
        if (neither.Value != PrivilegeNotHeld)
        {
            Console.WriteLine($"INCONCLUSIVE: the both-disabled arm failed 0x{(uint)neither.Value:X8}, not 0x80070522.");
            Console.WriteLine("Only ERROR_PRIVILEGE_NOT_HELD identifies a privilege gate; another code usually means");
            Console.WriteLine("the entry was not pristine (0x80070050) or something else went wrong first.");
            return;
        }
        if (!both.Succeeded)
        {
            Console.WriteLine($"INCONCLUSIVE: enabling both did not make the call succeed (0x{(uint)both.Value:X8}),");
            Console.WriteLine("so these two privileges are not sufficient on their own. Elevation is supplying");
            Console.WriteLine("something further; the gate is not fully explained by SeBackup/SeRestore.");
            return;
        }

        string sufficient =
            backupOnly.Succeeded && restoreOnly.Succeeded ? "EITHER one alone is sufficient"
            : backupOnly.Succeeded ? "SeBackupPrivilege ALONE is sufficient"
            : restoreOnly.Succeeded ? "SeRestorePrivilege ALONE is sufficient"
            : "BOTH are required together; neither alone suffices";
        Console.WriteLine($"ProcessBaseImage is gated on SeBackupPrivilege/SeRestorePrivilege — {sufficient}.");
        Console.WriteLine();
        Console.WriteLine("What this DOES establish: the call checks a privilege the token must have ENABLED, and");
        Console.WriteLine("an elevated token is not otherwise special for this purpose.");
        Console.WriteLine("What it does NOT establish: that Backup Operators membership makes `import` work");
        Console.WriteLine("unelevated. That is the obvious next hypothesis — the group grants these privileges —");
        Console.WriteLine("but it is untested here, and the full import does more than this one call. Verify it");
        Console.WriteLine("directly: add an account to Backup Operators, sign out and back in, then run");
        Console.WriteLine("`import` UNELEVATED and see whether it completes.");
    }
}
