// Token privilege enablement for the base-layer import path (issue #30).
// hcsshim's contract (internal/wclayer NewLayerWriter): the caller must hold
// SeBackupPrivilege and SeRestorePrivilege, enabled, for the whole life of the
// writer — every relative NtCreateFile uses FILE_OPEN_FOR_BACKUP_INTENT and
// every BackupWrite restores security data under them.
//
// The trap this file exists to encode: AdjustTokenPrivileges returns TRUE even
// when it enabled NOTHING, signalling the shortfall only via
// GetLastError() == ERROR_NOT_ALL_ASSIGNED. A filtered (unelevated) admin
// token does not HOLD these privileges at all, so ignoring that error would
// fail much later and confusingly — an ACCESS_DENIED mid-import, or a layer
// whose security descriptors were silently never restored.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace HcsContainerSpike;

internal static class TokenPrivileges
{
    public const string SeBackupPrivilege = "SeBackupPrivilege";
    public const string SeRestorePrivilege = "SeRestorePrivilege";

    private const int ErrorNotAllAssigned = 1300;
    private static readonly HRESULT NotAllAssignedHr = new(unchecked((int)0x80070514)); // HRESULT_FROM_WIN32(1300)

    /// <summary>Enables the named privileges on the process token, one call per
    /// privilege so the detail names exactly which one a token lacks. Returns
    /// S_OK only when every privilege is enabled.</summary>
    public static unsafe HRESULT EnableProcessPrivileges(IReadOnlyList<string> names, out string detail)
    {
        // GetCurrentProcess returns the process pseudo-handle — never closed.
        using SafeFileHandle process = new(PInvoke.GetCurrentProcess(), ownsHandle: false);
        if (!PInvoke.OpenProcessToken(
                process,
                TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY,
                out SafeFileHandle token))
        {
            HRESULT openHr = HrFromLastError();
            detail = $"OpenProcessToken failed: 0x{(uint)openHr.Value:X8}";
            return openHr;
        }

        using (token)
        {
            var notes = new List<string>();
            HRESULT result = default;
            foreach (string name in names)
            {
                HRESULT hr = EnableOne(token, name, out string note);
                notes.Add(note);
                if (hr.Failed && result.Succeeded)
                {
                    result = hr; // first failure decides; later notes still record
                }
            }
            detail = string.Join("; ", notes);
            return result;
        }
    }

    public static HRESULT EnableBackupRestorePrivileges(out string detail) =>
        EnableProcessPrivileges([SeBackupPrivilege, SeRestorePrivilege], out detail);

    private static unsafe HRESULT EnableOne(SafeFileHandle token, string name, out string note)
    {
        if (!PInvoke.LookupPrivilegeValue(null, name, out LUID luid))
        {
            HRESULT lookupHr = HrFromLastError();
            note = $"{name}: LookupPrivilegeValue failed 0x{(uint)lookupHr.Value:X8}";
            return lookupHr;
        }

        TOKEN_PRIVILEGES state = default;
        state.PrivilegeCount = 1;
        state.Privileges[0] = new LUID_AND_ATTRIBUTES
        {
            Luid = luid,
            Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED,
        };

        // The BOOL result only reports parameter-level failure. The interesting
        // outcome — "the token does not hold this privilege" — is TRUE +
        // ERROR_NOT_ALL_ASSIGNED, checked explicitly below.
        if (!PInvoke.AdjustTokenPrivileges(new HANDLE(token.DangerousGetHandle()), false, &state, 0, null, null))
        {
            HRESULT adjustHr = HrFromLastError();
            note = $"{name}: AdjustTokenPrivileges failed 0x{(uint)adjustHr.Value:X8}";
            return adjustHr;
        }
        if (Marshal.GetLastPInvokeError() == ErrorNotAllAssigned)
        {
            note = $"{name}: NOT HELD by this token (ERROR_NOT_ALL_ASSIGNED) — a filtered/unelevated token does not " +
                   "carry it; the import path needs an elevated (or Backup Operators) token";
            return NotAllAssignedHr;
        }

        note = $"{name}: enabled";
        return default;
    }

    private static HRESULT HrFromLastError()
    {
        int error = Marshal.GetLastPInvokeError();
        return new HRESULT(unchecked((int)0x80070000) | (error & 0xFFFF));
    }
}
