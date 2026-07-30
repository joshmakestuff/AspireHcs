using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AspireHcs.Hosting;

/// <summary>
/// Streams the guest's COM1 serial console (exposed by HCS as a named pipe) to the resource's
/// dashboard logs, line by line. Guests without a serial console configured (no
/// <c>console=ttyS0</c> on Linux) simply produce nothing — the pipe connects but stays silent.
/// </summary>
internal static class SerialConsolePump
{
    public static async Task RunAsync(string pipeName, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            for (int attempt = 0; !pipe.IsConnected && attempt < 60 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!pipe.IsConnected)
            {
                logger.LogDebug("Serial console pipe never connected; no guest console output will be shown.");
                return;
            }

            byte[] buffer = new byte[4096];
            StringBuilder line = new();
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                foreach (char c in Encoding.UTF8.GetString(buffer, 0, read))
                {
                    if (c == '\n')
                    {
                        logger.LogInformation("{SerialLine}", line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }

            if (line.Length > 0)
            {
                logger.LogInformation("{SerialLine}", line.ToString().TrimEnd('\r'));
            }
        }
        catch (OperationCanceledException)
        {
            // AppHost shutdown.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Serial console pump stopped unexpectedly.");
        }
    }
}
