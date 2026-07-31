using System.Runtime.Versioning;
using System.Text;
using AspireHcs.Hosting;
using Xunit;

namespace AspireHcs.Tests;

// Issue #18 acceptance: the serial console is guest-controlled input crossing a trust
// boundary, so the framer — not the guest — decides how much the host buffers. These pin the
// bound, the truncation visibility, the stateful UTF-8 decode across reads, and the terminal
// semantics of carriage returns.
[SupportedOSPlatform("windows10.0.17763")]
public class SerialLineFramerTests
{
    private readonly List<string> _emitted = [];

    private SerialLineFramer Framer(int maxLineLength = SerialLineFramer.DefaultMaxLineLength)
        => new(maxLineLength, _emitted.Add);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Lines_split_across_reads_are_reassembled()
    {
        SerialLineFramer framer = Framer();

        framer.Append(Bytes("boot: loa"));
        framer.Append(Bytes("ding initrd\nsecond "));
        framer.Append(Bytes("line\n"));

        Assert.Equal(["boot: loading initrd", "second line"], _emitted);
    }

    [Fact]
    public void Multibyte_utf8_split_across_reads_survives()
    {
        SerialLineFramer framer = Framer();
        byte[] bytes = Bytes("héllo wörld 🚀\n");

        // Feed one byte at a time: every multi-byte sequence is split across appends, which
        // the old per-read Encoding.UTF8.GetString decoded as replacement characters.
        foreach (byte b in bytes)
        {
            framer.Append([b]);
        }

        Assert.Equal(["héllo wörld 🚀"], _emitted);
        Assert.DoesNotContain('�', _emitted[0]);
    }

    [Fact]
    public void Overlong_line_is_truncated_once_visibly_and_bounded()
    {
        SerialLineFramer framer = Framer(maxLineLength: 32);

        // A newline-free stream far beyond the cap — the guest-driven-OOM shape. One marked
        // record, nothing else, no matter how much more arrives.
        for (int i = 0; i < 1000; i++)
        {
            framer.Append(Bytes(new string('A', 100)));
        }
        framer.Flush();

        string record = Assert.Single(_emitted);
        Assert.EndsWith("…[truncated]", record);
        Assert.StartsWith(new string('A', 32), record);
        Assert.True(record.Length < 64, $"truncated record is itself unbounded: {record.Length} chars");
    }

    [Fact]
    public void After_a_truncated_line_the_next_line_is_emitted_normally()
    {
        SerialLineFramer framer = Framer(maxLineLength: 16);

        framer.Append(Bytes("AAAAAAAAAAAAAAAAAAAAAAAA-swallowed-tail\nnext line\n"));

        Assert.Equal(2, _emitted.Count);
        Assert.EndsWith("…[truncated]", _emitted[0]);
        Assert.Equal("next line", _emitted[1]);
    }

    [Fact]
    public void A_line_of_exactly_the_cap_is_complete_not_truncated()
    {
        SerialLineFramer framer = Framer(maxLineLength: 8);

        framer.Append(Bytes("12345678\n123456789\n"));

        Assert.Equal(2, _emitted.Count);
        Assert.Equal("12345678", _emitted[0]);
        Assert.Equal("12345678 …[truncated]", _emitted[1]);
    }

    [Fact]
    public void An_overlong_crlf_terminated_line_leaves_no_blank_record_behind()
    {
        SerialLineFramer framer = Framer(maxLineLength: 8);

        // The CR ends the discard and the LF must go down with the truncated line — split
        // across reads too, since that is how a pipe delivers it.
        framer.Append(Bytes("AAAAAAAAAAAAAAAA\r"));
        framer.Append(Bytes("\nnext\r\n"));

        Assert.Equal(2, _emitted.Count);
        Assert.EndsWith("…[truncated]", _emitted[0]);
        Assert.Equal("next", _emitted[1]);
    }

    [Fact]
    public void A_cr_repaint_after_an_overlong_frame_still_emits_the_new_frame()
    {
        SerialLineFramer framer = Framer(maxLineLength: 8);

        framer.Append(Bytes("AAAAAAAAAAAAAAAA\rok\n"));

        Assert.Equal(2, _emitted.Count);
        Assert.EndsWith("…[truncated]", _emitted[0]);
        Assert.Equal("ok", _emitted[1]);
    }

    [Fact]
    public void Carriage_return_rewrites_emit_only_the_final_frame()
    {
        SerialLineFramer framer = Framer();

        framer.Append(Bytes("progress: 45%\rprogress: 67%\rprogress: 100%\n"));

        Assert.Equal(["progress: 100%"], _emitted);
    }

    [Fact]
    public void Crlf_line_endings_emit_one_record_per_line()
    {
        SerialLineFramer framer = Framer();

        framer.Append(Bytes("first\r\n"));
        // The CR and LF split across reads must not become two records either.
        framer.Append(Bytes("second\r"));
        framer.Append(Bytes("\nthird\r\n"));

        Assert.Equal(["first", "second", "third"], _emitted);
    }

    [Fact]
    public void A_cr_rewritten_line_never_grows_past_its_longest_frame()
    {
        SerialLineFramer framer = Framer(maxLineLength: 32);

        // An endless CR-only status stream (spinners, progress bars) must not trip the
        // truncation cap: each repaint resets the buffer, so no frame accumulates.
        for (int i = 0; i < 10_000; i++)
        {
            framer.Append(Bytes($"spin {i % 10}\r"));
        }
        framer.Append(Bytes("done\n"));

        Assert.Equal(["done"], _emitted);
    }

    [Fact]
    public void Flush_emits_the_unterminated_final_line()
    {
        SerialLineFramer framer = Framer();

        framer.Append(Bytes("no newline at end"));
        framer.Flush();

        Assert.Equal(["no newline at end"], _emitted);
    }

    [Fact]
    public void Flush_surfaces_a_dangling_partial_sequence_as_replacement()
    {
        SerialLineFramer framer = Framer();

        byte[] rocket = Bytes("🚀");
        framer.Append(Bytes("tail: "));
        framer.Append(rocket.AsSpan(0, 2).ToArray());
        framer.Flush();

        string record = Assert.Single(_emitted);
        Assert.StartsWith("tail: ", record);
        Assert.Contains('�', record);
    }
}
