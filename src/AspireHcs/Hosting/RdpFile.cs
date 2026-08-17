using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AspireHcs.Hosting;

/// <summary>
/// Builds the <c>.rdp</c> connection file mstsc consumes. The format is line-based
/// (<c>name:type:value</c>), which makes it a syntax boundary: a value carrying a newline
/// would not be escaped, it would become another setting. Every value goes through
/// <see cref="Field"/>.
/// </summary>
/// <remarks>
/// mstsc has no command-line switch for the user name; the file is the only route for it.
/// </remarks>
internal static class RdpFile
{
    /// <summary>
    /// UTF-16LE with a BOM, which is what mstsc itself writes when you save a connection.
    /// </summary>
    internal static readonly Encoding FileEncoding = new UnicodeEncoding(
        bigEndian: false, byteOrderMark: true);

    internal static string Build(string address, int port, string? userName)
    {
        StringBuilder content = new();
        content.AppendLine(Field("full address", 's', FormatFullAddress(address, port)));

        // Omitted entirely when unknown: an empty `username:s:` is not the same as absent —
        // it prefills the credential prompt with a blank user rather than the last one used.
        if (!string.IsNullOrEmpty(userName))
        {
            content.AppendLine(Field("username", 's', userName));
        }

        return content.ToString();
    }

    /// <summary>
    /// Renders one <c>name:type:value</c> line. Throws when the value cannot be represented in
    /// it; values are not sanitized.
    /// </summary>
    internal static string Field(string name, char type, string value)
    {
        ValidateValue(name, value);
        return string.Create(CultureInfo.InvariantCulture, $"{name}:{type}:{value}");
    }

    /// <summary>
    /// The representability rule. Also applied at model-build time.
    /// </summary>
    internal static void ValidateValue(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        foreach (char c in value)
        {
            // Controls generally, not just CR/LF: the parser's treatment of the rest is
            // undefined, and NUL in particular truncates the line in native consumers.
            if (char.IsControl(c))
            {
                throw new ArgumentException(
                    $"The value for .rdp setting '{name}' contains the control character U+{(int)c:X4}, " +
                    "which would corrupt the line-based file format.",
                    nameof(value));
            }
        }
    }

    /// <summary>
    /// Formats the <c>full address</c> host:port. IPv6 literals are bracketed, without which
    /// the colons in the address are indistinguishable from the port separator.
    /// </summary>
    internal static string FormatFullAddress(string address, int port)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        bool isIpv6 = IPAddress.TryParse(address, out IPAddress? parsed)
            && parsed.AddressFamily == AddressFamily.InterNetworkV6;

        return isIpv6
            ? string.Create(CultureInfo.InvariantCulture, $"[{address}]:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{address}:{port}");
    }
}
