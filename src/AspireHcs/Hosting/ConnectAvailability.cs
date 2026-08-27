using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace AspireHcs.Hosting;

/// <summary>
/// Resource-agnostic pieces shared by every connect command: the click-time state guard, the
/// fire-and-forget host launch, and the never-throw failure shape. Used by both
/// <see cref="ConnectCommands"/> (VM SSH/RDP) and <see cref="ContainerConnectCommands"/>
/// (container shell).
/// </summary>
internal static class ConnectAvailability
{
    /// <summary>
    /// Whether the state permits connecting. <c>null</c> (state unknown) is permitted: the
    /// resource id <see cref="CurrentState"/> looks up by is not guaranteed to equal the resource
    /// name. A command's own <c>Availability</c> has a real snapshot and requires Running.
    /// </summary>
    internal static bool StateAllowsConnect(string? currentState)
        => currentState is null || currentState == KnownResourceStates.Running;

    /// <summary>
    /// Best-effort current state for the guard above. Returns null when it cannot be determined.
    /// </summary>
    internal static string? CurrentState(IServiceProvider services, string resourceName)
    {
        try
        {
            ResourceNotificationService notifications =
                services.GetRequiredService<ResourceNotificationService>();
            return notifications.TryGetCurrentState(resourceName, out ResourceEvent? resourceEvent)
                ? resourceEvent.Snapshot.State?.Text
                : null;
        }
        catch (InvalidOperationException)
        {
            // The service is not registered in this host (for example a bare model in a test).
            // Unknown state permits connecting.
            return null;
        }
    }

    internal static void ShellExecute(ProcessStartInfo startInfo)
    {
        // Disposing the handle does not disturb the client: it keeps running independently.
        using Process? _ = Process.Start(startInfo);
    }

    internal static ExecuteCommandResult Failure(string message)
        => new() { Success = false, Message = message };
}
