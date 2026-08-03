// `identify` (issue #30 / #33 follow-up): name the privilege that gates layer
// finalization, so "a required privilege is not held" becomes a specific,
// grantable thing rather than a synonym for "run as admin".
//
// Why this matters: if the gate is SeBackupPrivilege/SeRestorePrivilege, then a
// developer in the Backup Operators group HOLDS them (disabled by default, but
// enableable via AdjustTokenPrivileges) and acquisition needs NO UAC prompt at
// all — a one-time group grant, exactly like the Hyper-V Administrators
// prerequisite the VM path already documents. If it is something else, that
// route is closed and the answer is worth knowing before anyone designs around
// it.
//
// The method is differential and needs elevation, because only an elevated
// token holds enough privileges for turning them OFF to be meaningful: run the
// same call twice against two IDENTICAL freshly-extracted trees, once with the
// candidate privileges disabled and once with them enabled. ProcessBaseImage is
// not idempotent, so each attempt requires its own untouched entry.

using Windows.Win32.Foundation;

namespace HcsContainerSpike;

internal static partial class Program
{
    private static int Identify(string[] args)
    {
        string entryDisabled = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Opt(args, "--entry-disabled")
            ?? throw new ArgumentException("--entry-disabled <freshly extracted entry> is required")));
        string entryEnabled = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Opt(args, "--entry-enabled")
            ?? throw new ArgumentException("--entry-enabled <a second, freshly extracted entry> is required")));

        Console.WriteLine();
        Console.WriteLine("--- Token privileges ---");
        IReadOnlyList<(string Name, bool Enabled)> privileges = TokenPrivileges.ListProcessPrivileges();
        if (privileges.Count == 0)
        {
            Console.WriteLine("  (none readable)");
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
            // Without the privileges in the token at all there is nothing to
            // toggle, and both arms would fail for the same trivial reason —
            // a differential test whose arms cannot differ proves nothing.
            Step("IdentifyPreconditions", ProbeFailed,
                "this token does not HOLD SeBackup/SeRestore, so disabling vs enabling them cannot be " +
                "distinguished. Run this elevated (or as a member of Backup Operators).");
            return 2;
        }

        // Arm 1: privileges present but DISABLED.
        HRESULT disableHr = TokenPrivileges.DisableProcessPrivileges(
            [TokenPrivileges.SeBackupPrivilege, TokenPrivileges.SeRestorePrivilege], out string disableDetail);
        Console.WriteLine();
        Console.WriteLine($"[arm 1] disable: {disableDetail}");
        HRESULT disabledResult = WcLayer.ProcessBase(entryDisabled);
        Step("ProcessBaseImage(SeBackup+SeRestore DISABLED)", disabledResult, entryDisabled);

        // Arm 2: the same call, same shape of input, privileges ENABLED.
        HRESULT enableHr = TokenPrivileges.EnableBackupRestorePrivileges(out string enableDetail);
        Console.WriteLine($"[arm 2] enable: {enableDetail}");
        if (enableHr.Failed)
        {
            Step("IdentifyPreconditions", enableHr, "could not re-enable the privileges; arm 2 would be meaningless");
            return 2;
        }
        HRESULT enabledResult = WcLayer.ProcessBase(entryEnabled);
        Step("ProcessBaseImage(SeBackup+SeRestore ENABLED)", enabledResult, entryEnabled);

        Console.WriteLine();
        Console.WriteLine("--- Verdict ---");
        const int PrivilegeNotHeld = unchecked((int)0x80070522);
        if (disabledResult.Value == PrivilegeNotHeld && enabledResult.Succeeded)
        {
            Console.WriteLine("SeBackupPrivilege/SeRestorePrivilege ARE the gate: the identical call fails");
            Console.WriteLine("0x80070522 with them disabled and succeeds with them enabled.");
            Console.WriteLine("CONSEQUENCE: a token that merely HOLDS them is enough, so membership in a group");
            Console.WriteLine("that grants them (Backup Operators) would let import run UNELEVATED — a one-time");
            Console.WriteLine("grant like Hyper-V Administrators, with no UAC prompt per image.");
        }
        else if (disabledResult.Succeeded)
        {
            Console.WriteLine("NOT the gate: the call succeeded with both privileges disabled, so elevation is");
            Console.WriteLine("supplying something else (another privilege, or an access check). Enumerate the");
            Console.WriteLine("remaining enabled privileges above and bisect from there.");
        }
        else
        {
            Console.WriteLine($"INCONCLUSIVE: disabled arm gave 0x{(uint)disabledResult.Value:X8}, enabled arm gave " +
                              $"0x{(uint)enabledResult.Value:X8}.");
            Console.WriteLine("Both arms must differ, and the disabled arm must fail with 0x80070522 specifically,");
            Console.WriteLine("for the privileges to be named as the gate. A shared failure usually means the");
            Console.WriteLine("entries were not pristine — ProcessBaseImage is not idempotent and fails");
            Console.WriteLine("0x80070050 against leftovers from an earlier attempt.");
        }
        _ = disableHr;
        return Results.Any(r => r.Hr.Failed) && enabledResult.Failed ? 2 : 0;
    }
}
