using System.Globalization;
using System.Text.RegularExpressions;

namespace AspireHcs.Cli;

/// <summary>
/// Classifies hcsctl failure text by the HRESULTs it embeds. hcsctl formats every HRESULT as a
/// lowercase <c>0x%08x</c> (<c>hcsctl/internal/computecore/computecore.go</c>), and HCS can
/// surface the same result with or without the customer bit set, so matching normalizes the
/// value rather than comparing text. No HRESULT matcher existed anywhere in AspireHcs before
/// this; failure classification was string containment only.
/// </summary>
internal static partial class HcsErrors
{
    /// <summary>
    /// HCS_E_INVALID_STATE as hcsctl prints the stable spelling: 0x037 facility, 0x105 code, the
    /// severity bit set and the customer bit clear — 0x80370105. The severity bit is part of the
    /// value: a success-flagged form (0x00370105, 0x40370105) is not this error and must not
    /// match.
    /// </summary>
    private const uint InvalidStateCode = 0x80370105;

    /// <summary>
    /// The customer bit 0x40000000 is the one spelling noise between the forms hcsctl can
    /// produce: HCS surfaces the same result with it set (0xc0370105) or clear (0x80370105).
    /// Only it is masked — hcsctl's own matcher clears just this bit for the same reason, and
    /// masking the severity bit too would make success forms match.
    /// </summary>
    private const uint CustomerBit = 0x40000000;

    /// <summary>
    /// An hcsctl-printed HRESULT: <c>0x</c> followed by exactly eight hex digits, and not the
    /// prefix of a longer hex literal.
    /// </summary>
    [GeneratedRegex(@"0x[0-9a-fA-F]{8}(?![0-9a-fA-F])")]
    private static partial Regex HresultRegex();

    /// <summary>
    /// Reports whether <paramref name="message"/> — an hcsctl failure message, e.g. the
    /// <see cref="HcsCtlCommandException"/> text built by <see cref="HcsCtl"/>'s Interpret —
    /// embeds HCS_E_INVALID_STATE in either spelling hcsctl can produce (0x80370105 or
    /// 0xc0370105). Every token is scanned, so an unrelated HRESULT earlier in the message
    /// cannot mask a later match.
    /// </summary>
    public static bool IsInvalidState(string? message)
    {
        foreach (string token in HresultTokens(message))
        {
            if (uint.TryParse(
                    token.AsSpan(2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out uint code)
                && (code & ~CustomerBit) == InvalidStateCode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every HRESULT token in <paramref name="message"/>, verbatim and in order. Empty when the
    /// message is null, empty, or carries no token. Internal so the regex lives in one place and
    /// the classifier stays testable through this single surface.
    /// </summary>
    internal static IReadOnlyList<string> HresultTokens(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        return HresultRegex().Matches(message).Select(m => m.Value).ToArray();
    }
}
