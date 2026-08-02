// Micro self-tests for the native building blocks of the import path (issue
// #30): each new P/Invoke surface earns a runnable probe here BEFORE the import
// loop composes them, so a marshalling bug (UNICODE_STRING byte lengths,
// packed WIN32_STREAM_ID headers, token privilege plumbing) dies in a 1-second
// run against scratch state instead of surfacing mid-way through a 400 MB
// layer import.
//
// Outcomes are recorded through the same Step/Results machinery as every other
// mode. Privilege-dependent rows (token enablement) record what the CURRENT
// token produced — run both elevated and unelevated to see both sides.

using Windows.Win32.Foundation;

namespace HcsContainerSpike;

internal static partial class Program
{
    private static int SelfTest(string[] args)
    {
        string workDir = Opt(args, "--work") ?? Path.Combine(Path.GetTempPath(), "AspireHcsSelfTest");

        // Token privileges: the outcome depends on the token, so the row states
        // which result THIS run proves. Elevated: both enable. Unelevated: the
        // distinct ERROR_NOT_ALL_ASSIGNED path, which import relies on to fail
        // fast instead of dying confusingly mid-import.
        HRESULT hr = TokenPrivileges.EnableBackupRestorePrivileges(out string detail);
        bool elevated = IsElevated();
        bool expected = elevated ? hr.Succeeded : hr.Value == unchecked((int)0x80070514);
        Step($"SelfTest(token privileges, elevated={elevated})",
            expected ? default : hr.Succeeded ? ProbeFailed : hr,
            $"{detail}{(expected ? "" : " — UNEXPECTED for this elevation")}");

        return Results.Any(r => r.Hr.Failed) ? 2 : 0;
    }
}
