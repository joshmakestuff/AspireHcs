using System.Text.Json;
using AspireHcs.Hcs.Schema;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AspireHcs.Hcs;

/// <summary>
/// Entry points for the Host Compute System service. The Aspire resource layer talks to this
/// (and <see cref="HcsComputeSystem"/>) only — never to a P/Invoke.
/// </summary>
internal static class HcsClient
{
    /// <summary>Creates (but does not start) a compute system from a schema document.</summary>
    public static async Task<HcsComputeSystem> CreateComputeSystemAsync(string id, ComputeSystemDocument document, CancellationToken cancellationToken = default)
    {
        HcsPlatform.ThrowIfUnsupported();
        string config = JsonSerializer.Serialize(document, HcsJsonContext.Default.ComputeSystemDocument);

        using HcsOperation op = new();
        HRESULT hr = PInvoke.HcsCreateComputeSystem(id, config, op.Handle, null, out HcsCloseComputeSystemSafeHandle handle);
        if (hr.Failed)
        {
            handle.Dispose();
            throw HcsException.Create("HcsCreateComputeSystem", hr, resultDocument: null);
        }

        try
        {
            await op.WaitForResultAsync("HcsCreateComputeSystem", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new HcsComputeSystem(id, handle);
    }

    /// <summary>Opens an existing compute system by id, e.g. to terminate a leftover VM.</summary>
    public static HcsComputeSystem OpenComputeSystem(string id)
    {
        HcsPlatform.ThrowIfUnsupported();
        const uint GenericAll = 0x10000000;
        HRESULT hr = PInvoke.HcsOpenComputeSystem(id, GenericAll, out HcsCloseComputeSystemSafeHandle handle);
        if (hr.Failed)
        {
            handle.Dispose();
            throw HcsException.Create("HcsOpenComputeSystem", hr, resultDocument: null);
        }

        return new HcsComputeSystem(id, handle);
    }

    /// <summary>Returns the raw JSON array of compute systems matching the query document.</summary>
    public static async Task<string?> EnumerateComputeSystemsAsync(string query = "{}", CancellationToken cancellationToken = default)
    {
        HcsPlatform.ThrowIfUnsupported();
        using HcsOperation op = new();
        HRESULT hr = PInvoke.HcsEnumerateComputeSystems(query, op.Handle);
        if (hr.Failed)
        {
            throw HcsException.Create("HcsEnumerateComputeSystems", hr, resultDocument: null);
        }

        return await op.WaitForResultAsync("HcsEnumerateComputeSystems", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Grants the VM's virtual identity access to a backing file (VHDX, ISO, ...). The grant is
    /// a persistent ACE on the file — FullControl, empirically, not read-only — so every grant
    /// must be paired with <see cref="RevokeVmAccess"/> at teardown or the file's ACL grows by
    /// one dead VM identity per run.
    /// </summary>
    public static void GrantVmAccess(string id, string filePath)
    {
        HcsPlatform.ThrowIfUnsupported();
        HRESULT hr = PInvoke.HcsGrantVmAccess(id, filePath);
        if (hr.Failed)
        {
            throw HcsException.Create("HcsGrantVmAccess", hr, resultDocument: null);
        }
    }

    /// <summary>
    /// Revokes a grant made via <see cref="GrantVmAccess"/>. Throws on failure like every other
    /// call here — whether a failed revocation is fatal is the caller's decision (the teardown
    /// ledger logs it and continues; a silently swallowed HRESULT would leave the ACL entry in
    /// place with no trace that it survived).
    /// </summary>
    public static void RevokeVmAccess(string id, string filePath)
    {
        HcsPlatform.ThrowIfUnsupported();
        HRESULT hr = PInvoke.HcsRevokeVmAccess(id, filePath);
        if (hr.Failed)
        {
            throw HcsException.Create("HcsRevokeVmAccess", hr, resultDocument: null);
        }
    }
}
