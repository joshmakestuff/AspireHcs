using System.Runtime.Versioning;
using AspireHcs.Cli;
using Xunit;

namespace AspireHcs.Tests;

// Pause-before-workload (#74): hcsctl formats HRESULTs as lowercase `0x%08x` and the same HCS
// result can surface with or without the customer bit set, so the recovery path has to classify
// failure text by value, not by exact spelling. These pin the classifier: what reads as
// HCS_E_INVALID_STATE, what does not, and which tokens the regex hands back.
[SupportedOSPlatform("windows10.0.17763")]
public class HcsErrorsTests
{
    [Fact]
    public void Invalid_state_with_the_customer_bit_is_recognized()
    {
        const string message =
            """CreateProcess("ping -t 127.0.0.1"): container x encountered an error during hcs::System::CreateProcess: ... (0xc0370105)""";

        Assert.True(HcsErrors.IsInvalidState(message));
    }

    [Fact]
    public void Invalid_state_without_the_customer_bit_is_recognized()
    {
        const string message =
            """CreateProcess("ping -t 127.0.0.1"): container x encountered an error during hcs::System::CreateProcess: ... (0x80370105)""";

        Assert.True(HcsErrors.IsInvalidState(message));
    }

    [Fact]
    public void Uppercase_hex_digits_are_recognized()
    {
        Assert.True(HcsErrors.IsInvalidState("... (0xC0370105)"));
    }

    [Fact]
    public void An_unrelated_hresult_is_not_invalid_state()
    {
        Assert.False(HcsErrors.IsInvalidState("ping: 0x8007000d (The data is invalid)"));
    }

    [Fact]
    public void Success_flagged_forms_of_the_code_are_not_invalid_state()
    {
        // Only the customer bit 0x40000000 is masked: the severity bit is part of the value, so
        // a success-flagged form of the code (HRESULT semantics: not an error) must not match.
        Assert.False(HcsErrors.IsInvalidState("... (0x0370105)"));
        Assert.False(HcsErrors.IsInvalidState("... (0x00370105)"));
        Assert.False(HcsErrors.IsInvalidState("... (0x40370105)"));
    }

    [Fact]
    public void Null_and_empty_messages_are_not_invalid_state()
    {
        Assert.False(HcsErrors.IsInvalidState(null));
        Assert.False(HcsErrors.IsInvalidState(string.Empty));
    }

    [Fact]
    public void A_message_without_a_hresult_is_not_invalid_state()
    {
        Assert.False(HcsErrors.IsInvalidState("container x failed to start"));
    }

    [Fact]
    public void An_earlier_unrelated_hresult_does_not_mask_a_later_match()
    {
        const string message =
            """hcsctl failed: ping: 0x8007000d (The data is invalid): container x encountered an error during hcs::System::CreateProcess: ... (0xc0370105)""";

        Assert.True(HcsErrors.IsInvalidState(message));
    }

    [Fact]
    public void Tokens_are_returned_verbatim_and_in_order()
    {
        const string message = "ping: 0x8007000d (The data is invalid): ... (0xc0370105)";

        Assert.Equal(new[] { "0x8007000d", "0xc0370105" }, HcsErrors.HresultTokens(message));
    }

    [Fact]
    public void No_tokens_when_the_message_has_none()
    {
        Assert.Empty(HcsErrors.HresultTokens(null));
        Assert.Empty(HcsErrors.HresultTokens(string.Empty));
        Assert.Empty(HcsErrors.HresultTokens("no hex here"));
    }

    [Fact]
    public void A_longer_hex_literal_is_not_prefix_matched()
    {
        // hcsctl prints exactly eight hex digits (%08x); a longer literal must not yield the
        // first eight as a token.
        Assert.Empty(HcsErrors.HresultTokens("0xdeadbeefcafe"));
    }
}
