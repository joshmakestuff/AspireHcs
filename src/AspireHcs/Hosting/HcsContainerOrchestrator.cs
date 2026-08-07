using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using AspireHcs.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// Drives the lifecycle of an <see cref="HcsContainerResource"/> through Aspire's eventing
/// pipeline, mirroring <see cref="HcsVmOrchestrator"/>'s shape: boot on
/// <see cref="InitializeResourceEvent"/>, publish state to the dashboard, tear down on shutdown,
/// and register the Start/Stop/Restart commands Aspire wires up only for resources DCP owns.
/// </summary>
internal static class HcsContainerOrchestrator
{
    /// <summary>
    /// The state text for a paused container.
    /// </summary>
    /// <remarks>
    /// Aspire has no <c>Paused</c> in <see cref="KnownResourceStates"/> — checked, not assumed —
    /// so this is our own string. It is deliberately not in <c>TerminalStates</c>: a paused
    /// container is still there, and treating it as stopped would offer Start on something that
    /// never stopped, while hiding the Stop that would actually clean it up.
    /// </remarks>
    public const string PausedState = "Paused";

    public static void Register(IResourceBuilder<HcsContainerResource> builder)
    {
        InstanceHolder holder = new();

        builder.ApplicationBuilder.Eventing.Subscribe<InitializeResourceEvent>(builder.Resource, (@event, cancellationToken) =>
        {
            HcsContainerInstance instance = new(
                (HcsContainerResource)@event.Resource,
                @event.Services,
                @event.Eventing,
                @event.Notifications,
                @event.Logger);

            holder.Instance = instance;

            // The event handler must not block orchestration; the boot runs in the background
            // and reports through ResourceNotificationService.
            _ = Task.Run(() => instance.RunAsync(), CancellationToken.None);
            return Task.CompletedTask;
        });

        builder.WithCommand(
            KnownResourceCommands.StartCommand,
            "Start",
            context => ExecuteAsync(holder, i => i.StartAsync(), "started"),
            new CommandOptions
            {
                Description = "Boot the container.",
                IconName = "Play",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true,
                UpdateState = context =>
                {
                    string? state = State(context);
                    if (IsStopped(state))
                    {
                        return ResourceCommandState.Enabled;
                    }
                    return IsInFlight(state) ? ResourceCommandState.Disabled : ResourceCommandState.Hidden;
                },
            });

        builder.WithCommand(
            KnownResourceCommands.StopCommand,
            "Stop",
            context => ExecuteAsync(holder, i => i.StopAsync(), "stopped"),
            new CommandOptions
            {
                Description = "Stop the container and remove it.",
                IconName = "Stop",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true,
                UpdateState = context =>
                {
                    string? state = State(context);
                    if (state == KnownResourceStates.Stopping)
                    {
                        return ResourceCommandState.Disabled;
                    }
                    return IsStopped(state) || state == KnownResourceStates.Starting
                        ? ResourceCommandState.Hidden
                        : ResourceCommandState.Enabled;
                },
            });

        builder.WithCommand(
            KnownResourceCommands.RestartCommand,
            "Restart",
            context => ExecuteAsync(holder, i => i.RestartAsync(), "restarted"),
            new CommandOptions
            {
                Description = "Stop the container and boot it again from a fresh scratch layer.",
                IconName = "ArrowCounterclockwise",
                IconVariant = IconVariant.Regular,
                UpdateState = context => State(context) == KnownResourceStates.Running
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Disabled,
            });

        // Pause and Resume are each offered only in the one state they apply to, and hidden
        // otherwise rather than shown-but-disabled: an unavailable command should not look like a
        // broken one (#52).
        builder.WithCommand(
            "container-pause",
            "Pause",
            context => ExecuteAsync(holder, i => i.PauseAsync(), "paused"),
            new CommandOptions
            {
                Description = "Suspend the container. Its workload stops making progress.",
                IconName = "Pause",
                IconVariant = IconVariant.Filled,
                UpdateState = context => State(context) == KnownResourceStates.Running
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Hidden,
            });

        builder.WithCommand(
            "container-resume",
            "Resume",
            context => ExecuteAsync(holder, i => i.ResumeAsync(), "resumed"),
            new CommandOptions
            {
                Description = "Resume a paused container.",
                IconName = "Play",
                IconVariant = IconVariant.Regular,
                UpdateState = context => State(context) == PausedState
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Hidden,
            });

        builder.WithCommand(
            "container-ps",
            "List processes",
            context => ReportAsync(holder, i => i.ListGuestProcessesAsync()),
            new CommandOptions
            {
                Description = "Write the guest's process list to this resource's logs. Flat — HCS reports no parent pids.",
                IconName = "TextBulletListSquare",
                IconVariant = IconVariant.Regular,
                UpdateState = context => State(context) is { } state
                    && (state == KnownResourceStates.Running || state == PausedState)
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Hidden,
            });

        static string? State(UpdateCommandStateContext context) => context.ResourceSnapshot.State?.Text;

        static bool IsInFlight(string? state) =>
            state == KnownResourceStates.Starting || state == KnownResourceStates.Stopping;

        static bool IsStopped(string? state) =>
            KnownResourceStates.TerminalStates.Contains(state)
            || state == KnownResourceStates.NotStarted
            || state == "Unknown"
            || string.IsNullOrEmpty(state);
    }

    /// <summary>
    /// Runs a command whose own return value is the message worth showing — a process count, say —
    /// rather than a fixed past-tense confirmation.
    /// </summary>
    private static async Task<ExecuteCommandResult> ReportAsync(
        InstanceHolder holder, Func<HcsContainerInstance, Task<string>> action)
    {
        if (holder.Instance is not { } instance)
        {
            return new ExecuteCommandResult { Success = false, Message = "The container has not been initialized yet." };
        }

        try
        {
            return new ExecuteCommandResult { Success = true, Message = await action(instance).ConfigureAwait(false) };
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult { Success = false, Message = ex.Message };
        }
    }

    private static async Task<ExecuteCommandResult> ExecuteAsync(
        InstanceHolder holder, Func<HcsContainerInstance, Task> action, string pastTense)
    {
        if (holder.Instance is not { } instance)
        {
            return new ExecuteCommandResult { Success = false, Message = "The container has not been initialized yet." };
        }

        try
        {
            await action(instance).ConfigureAwait(false);
            return new ExecuteCommandResult { Success = true, Message = $"Container {pastTense}." };
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Removes containers left behind by dead AppHost processes. They outlive their owner —
    /// the compute system is host-global and hcsctl's state.json is on disk — so nothing reclaims
    /// them but this.
    /// </summary>
    /// <remarks>
    /// Deletion requires <em>proof of abandonment</em>: an id this integration wrote, whose
    /// recorded pid is not running. The same discipline as the VM path's endpoint scavenging
    /// (#21), including its ordering invariant — see <see cref="SelectScavengeable"/>.
    /// </remarks>
    internal static async Task ScavengeAbandonedContainersAsync(
        HcsCtl hcsctl, string ownContainerId, ILogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            // ORDER MATTERS: containers are enumerated BEFORE the pid snapshot. A container in
            // this list was created by a process that existed before the snapshot, so if that
            // process is alive now it is in the snapshot — a recycled pid can therefore only make
            // a dead run look alive (deferring deletion), never a live run look dead. Snapshotting
            // pids first would open exactly that hole.
            HcsCtlContainerListDocument listing = await hcsctl.ListAsync(cancellationToken).ConfigureAwait(false);
            if (listing.Containers.Count == 0)
            {
                return;
            }

            HashSet<int> livePids = [.. Process.GetProcesses().Select(static p => p.Id)];

            foreach (string id in SelectScavengeable(listing, ownContainerId, livePids.Contains))
            {
                // Guarded per container: concurrent AppHosts may sweep the same leftovers, and
                // losing that race on one must not abort the rest of the sweep.
                try
                {
                    logger.LogInformation("Scavenging container {ContainerId} left by a dead run.", id);
                    await hcsctl.RemoveAsync(id, force: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping container {ContainerId} during scavenging.", id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scavenging abandoned containers failed; continuing.");
        }
    }

    /// <summary>
    /// Decides which listed containers are abandoned leftovers. Pure, so every rule is testable
    /// without a host: anything not written by this integration, owned by a live process, or
    /// belonging to this run is left alone.
    /// </summary>
    internal static IEnumerable<string> SelectScavengeable(
        HcsCtlContainerListDocument listing, string ownContainerId, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(listing);

        foreach (HcsCtlContainerRow row in listing.Containers)
        {
            if (row.Id is not { Length: > 0 } id || string.Equals(id, ownContainerId, StringComparison.Ordinal))
            {
                continue;
            }

            // An id this integration did not write belongs to somebody else — another tool, or a
            // person at a shell. The prefix is not a licence to delete; the pid is.
            if (HcsContainerResource.OwnerProcessId(id) is not { } pid)
            {
                continue;
            }

            if (isProcessAlive(pid))
            {
                continue;
            }

            yield return id;
        }
    }

    private sealed class InstanceHolder
    {
        private HcsContainerInstance? _instance;

        public HcsContainerInstance? Instance
        {
            get => Volatile.Read(ref _instance);
            set => Volatile.Write(ref _instance, value);
        }
    }
}
