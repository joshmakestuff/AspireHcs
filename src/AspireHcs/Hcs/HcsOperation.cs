using Windows.Win32;
using Windows.Win32.Foundation;

namespace AspireHcs.Hcs;

/// <summary>
/// RAII wrapper for an HCS_OPERATION handle with a Task-based wait.
/// <see cref="PInvoke.HcsWaitForOperationResult"/> is a blocking call, so the wait runs on the
/// thread pool; HCS operations are one-shot, so each wrapper is used for a single call.
/// </summary>
internal sealed unsafe class HcsOperation : IDisposable
{
    public HcsCloseOperationSafeHandle Handle { get; } = PInvoke.HcsCreateOperation_SafeHandle(null, null);

    /// <summary>
    /// Waits for the operation to complete and returns its result document.
    /// Throws <see cref="HcsException"/> (with the result document attached) on failure.
    /// </summary>
    public Task<string?> WaitForResultAsync(string step, uint timeoutMs = 30_000, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            HRESULT hr = PInvoke.HcsWaitForOperationResult(Handle, timeoutMs, out PWSTR document);
            string? text = ReadAndFree(document);
            return hr.Failed ? throw HcsException.Create(step, hr, text) : text;
        }, cancellationToken);

    private static unsafe string? ReadAndFree(PWSTR document)
    {
        if (document.Value == null)
        {
            return null;
        }

        string text = document.ToString();
        PInvoke.LocalFree(new HLOCAL(document.Value));
        return text;
    }

    public void Dispose() => Handle.Dispose();
}
