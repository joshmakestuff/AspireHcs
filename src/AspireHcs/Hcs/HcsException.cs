using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Win32.Foundation;

namespace AspireHcs.Hcs;

/// <summary>
/// Raised when an HCS call fails. Carries the raw HRESULT (via <see cref="Exception.HResult"/>)
/// and the HCS result document, so callers see the service's actual error message rather than
/// an opaque failure.
/// </summary>
internal sealed class HcsException : Exception
{
    private HcsException(string message, string step, int hresult, string? resultDocument)
        : base(message)
    {
        HResult = hresult;
        Step = step;
        ResultDocument = resultDocument;
    }

    /// <summary>The HCS API that failed, e.g. "HcsStartComputeSystem".</summary>
    public string Step { get; }

    /// <summary>The JSON result document returned by the operation, when one exists.</summary>
    public string? ResultDocument { get; }

    internal static HcsException Create(string step, HRESULT hr, string? resultDocument)
    {
        StringBuilder message = new($"{step} failed with HRESULT 0x{(uint)hr.Value:X8}");

        string? systemMessage = Marshal.GetExceptionForHR(hr.Value)?.Message;
        if (!string.IsNullOrWhiteSpace(systemMessage))
        {
            message.Append(": ").Append(systemMessage.TrimEnd('.'));
        }

        if (ExtractServiceMessage(resultDocument) is { } serviceMessage)
        {
            message.Append(" — ").Append(serviceMessage);
        }

        return new HcsException(message.ToString(), step, hr.Value, resultDocument);
    }

    private static string? ExtractServiceMessage(string? resultDocument)
    {
        if (string.IsNullOrWhiteSpace(resultDocument))
        {
            return null;
        }

        try
        {
            JsonNode? root = JsonNode.Parse(resultDocument);
            string? errorMessage = root?["ErrorMessage"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                return errorMessage;
            }

            if (root?["ErrorEvents"] is JsonArray events)
            {
                string?[] messages = [.. events.Select(e => e?["Message"]?.GetValue<string>())];
                string joined = string.Join("; ", messages.Where(m => !string.IsNullOrWhiteSpace(m)));
                if (joined.Length > 0)
                {
                    return joined;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON — fall through and let the caller inspect ResultDocument directly.
        }

        return null;
    }
}
