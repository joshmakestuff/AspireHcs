using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.HostComputeSystem;

namespace AspireHcs.Hcs;

/// <summary>
/// A running (or startable) HCS compute system. Wraps the HCS_SYSTEM handle; disposing the last
/// handle terminates the VM when the config sets ShouldTerminateOnLastHandleClosed.
/// </summary>
internal sealed class HcsComputeSystem : IDisposable
{
    private readonly HcsCloseComputeSystemSafeHandle _handle;
    private HCS_EVENT_CALLBACK? _callback; // rooted so the native side never calls a collected delegate

    internal HcsComputeSystem(string id, HcsCloseComputeSystemSafeHandle handle)
    {
        Id = id;
        _handle = handle;
        RegisterCallback();
    }

    public string Id { get; }

    /// <summary>Raised for HCS notifications (state changes, exit, ...) on a native callback thread.</summary>
    public event EventHandler<HcsNotification>? Notification;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => RunAsync("HcsStartComputeSystem", op => PInvoke.HcsStartComputeSystem(_handle, op.Handle, null), 60_000, cancellationToken);

    /// <summary>
    /// Graceful shutdown via the guest shutdown integration service (hv_utils on Linux,
    /// vmicshutdown on Windows). Two empirically-determined quirks are encoded here:
    /// the config document must declare <c>VirtualMachine.Services.Shutdown</c> at
    /// SchemaVersion 2.5+ with the mechanism forced to IntegrationService (the default is
    /// GuestConnection, which plain VMs without GCS lack → ERROR_NOT_SUPPORTED), and the IC
    /// channel finishes negotiating *after* the guest is otherwise ready, returning
    /// ERROR_DEVICE_NOT_AVAILABLE (0x800710DF) in the meantime — so not-ready errors are
    /// retried until <paramref name="readyTimeout"/> (default 60 s) elapses.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan? readyTimeout = null, CancellationToken cancellationToken = default)
    {
        const int ErrorNotReady = unchecked((int)0x80070015);
        const int ErrorDeviceNotAvailable = unchecked((int)0x800710DF);
        const string options = """{"Mechanism": "IntegrationService", "Type": "Shutdown"}""";

        DateTime deadline = DateTime.UtcNow + (readyTimeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                await RunAsync("HcsShutDownComputeSystem", op => PInvoke.HcsShutDownComputeSystem(_handle, op.Handle, options), 60_000, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HcsException ex) when (ex.HResult is ErrorNotReady or ErrorDeviceNotAvailable && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task TerminateAsync(CancellationToken cancellationToken = default)
        => RunAsync("HcsTerminateComputeSystem", op => PInvoke.HcsTerminateComputeSystem(_handle, op.Handle, null), 30_000, cancellationToken);

    public Task<string?> GetPropertiesAsync(string propertyQuery = "{}", CancellationToken cancellationToken = default)
        => RunAsync("HcsGetComputeSystemProperties", op => PInvoke.HcsGetComputeSystemProperties(_handle, op.Handle, propertyQuery), 30_000, cancellationToken);

    public Task<string?> ModifyAsync(string settingsDocument, CancellationToken cancellationToken = default)
        => RunAsync("HcsModifyComputeSystem", op => PInvoke.HcsModifyComputeSystem(_handle, op.Handle, settingsDocument, null), 30_000, cancellationToken);

    /// <summary>
    /// Waits until the guest OS has loaded its Hyper-V integration drivers, by asking for a
    /// memory size the guest must actually honour: HCS refuses the request with ERROR_NOT_READY
    /// (0x80070015) until the guest's balloon driver is up, then grants it.
    /// </summary>
    /// <remarks>
    /// The probe must request a size that <em>differs</em> from the configured one. Asking for the
    /// size the VM already has is a no-op that the VM worker satisfies on its own: it returns in
    /// ~40 ms having never consulted the guest, which is how this method previously claimed a
    /// still-booting VM was ready. Measured on the reference image, the real transition lands at
    /// ~9.3 s, ahead of the guest's first DHCP lease at ~14.4 s — see the
    /// <c>spikes/GuestReadinessProbeSpike</c> experiment, which asserts the distinction so it
    /// cannot silently regress. The probe grows rather than shrinks: shrinking pushed the reference guest into
    /// memory pressure it did not recover from.
    /// <para>
    /// This reports guest <em>kernel</em> readiness, not that a workload is listening. Use a
    /// health check against the resource's endpoint for that.
    /// </para>
    /// </remarks>
    public async Task WaitForGuestReadyAsync(int memoryMb, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        const int ProbeDeltaMb = 256;

        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await ModifyAsync(ResizeDocument(memoryMb + ProbeDeltaMb), cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (HcsException ex)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"The guest did not report ready within {timeout}. The probe resizes memory, which " +
                        "requires the guest's Hyper-V integration drivers (hv_balloon on Linux); an image " +
                        $"without them will never satisfy it. Last HCS error: 0x{ex.HResult:X8}.", ex);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await ModifyAsync(ResizeDocument(memoryMb), cancellationToken).ConfigureAwait(false);
        }
        catch (HcsException)
        {
            // Cosmetic only — the guest keeps the probe's extra headroom for this run.
        }

        static string ResizeDocument(int sizeMb) => $$"""
            {
                "ResourcePath": "VirtualMachine/ComputeTopology/Memory/SizeInMB",
                "RequestType": "Update",
                "Settings": {{sizeMb}}
            }
            """;
    }

    private async Task<string?> RunAsync(string step, Func<HcsOperation, HRESULT> invoke, uint timeoutMs, CancellationToken cancellationToken)
    {
        using HcsOperation op = new();
        HRESULT hr = invoke(op);
        if (hr.Failed)
        {
            throw HcsException.Create(step, hr, resultDocument: null);
        }

        return await op.WaitForResultAsync(step, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private unsafe void RegisterCallback()
    {
        _callback = OnNativeEvent;
        HRESULT hr = PInvoke.HcsSetComputeSystemCallback(_handle, HCS_EVENT_OPTIONS.HcsEventOptionNone, null, _callback);
        if (hr.Failed)
        {
            _handle.Dispose();
            throw HcsException.Create("HcsSetComputeSystemCallback", hr, resultDocument: null);
        }
    }

    private unsafe void OnNativeEvent(HCS_EVENT* @event, void* context)
        => Notification?.Invoke(this, new HcsNotification(@event->Type, @event->EventData.Value == null ? null : @event->EventData.ToString()));

    public void Dispose()
    {
        _handle.Dispose();
        _callback = null;
    }
}

/// <summary>An HCS notification: the event type plus its JSON payload, when one exists.</summary>
internal sealed record HcsNotification(HCS_EVENT_TYPE Type, string? EventData);
