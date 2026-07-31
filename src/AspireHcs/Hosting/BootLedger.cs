using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// Records what a single boot has acquired — work directory, HCN endpoint, ACL grants, the
/// compute system itself — as release actions, and releases them in reverse acquisition order
/// on <see cref="Drain"/>. Teardown correctness must not depend on how far a boot got before
/// failing: whatever was acquired is in the ledger, and each release runs even when another
/// throws.
/// </summary>
internal sealed class BootLedger(ILogger logger)
{
    private readonly Lock _lock = new();
    private readonly Stack<(string Description, Action Release)> _entries = new();
    private bool _sealed;

    /// <summary>
    /// Records the release action for a resource the boot just acquired. If the ledger has
    /// already been drained (AppHost shutdown won the race against an in-flight boot), the
    /// resource is released on the spot and the boot is cancelled — nothing acquired after a
    /// drain can outlive it.
    /// </summary>
    public void Add(string description, Action release)
    {
        lock (_lock)
        {
            if (!_sealed)
            {
                _entries.Push((description, release));
                return;
            }
        }

        Release(description, release);
        throw new OperationCanceledException(
            $"The run ended while '{description}' was being acquired; it has been released.");
    }

    /// <summary>
    /// Releases everything in reverse acquisition order and seals the ledger. Idempotent and
    /// safe to call from multiple threads — each entry is released exactly once, and a failing
    /// release is logged without stopping the rest.
    /// </summary>
    public void Drain()
    {
        (string Description, Action Release)[] entries;
        lock (_lock)
        {
            _sealed = true;
            // Stack enumerates most-recently-pushed first, which is exactly the release order.
            entries = [.. _entries];
            _entries.Clear();
        }

        foreach ((string description, Action release) in entries)
        {
            Release(description, release);
        }
    }

    private void Release(string description, Action release)
    {
        try
        {
            release();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release {Resource}; continuing with the remaining teardown.", description);
        }
    }
}
