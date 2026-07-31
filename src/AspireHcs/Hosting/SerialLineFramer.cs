using System.Text;

namespace AspireHcs.Hosting;

/// <summary>
/// Turns the guest-controlled byte stream of a serial console into bounded log records. The
/// guest sits on the other side of a trust boundary: nothing it writes may grow host memory
/// without limit, so a line longer than <paramref name="maxLineLength"/> is emitted once,
/// truncated and visibly marked, and the rest of that line is discarded. Decoding is stateful
/// so UTF-8 sequences split across pipe reads survive intact, and carriage returns get
/// terminal semantics — a CR-rewritten progress line emits its final frame, not one record
/// per repaint.
/// </summary>
internal sealed class SerialLineFramer(int maxLineLength, Action<string> emit)
{
    public const int DefaultMaxLineLength = 8192;

    private const string TruncationMarker = " …[truncated]";

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private char[] _chars = new char[1024];
    private bool _discardingOverlongLine;
    private bool _pendingCarriageReturn;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        int needed = Encoding.UTF8.GetMaxCharCount(bytes.Length);
        if (_chars.Length < needed)
        {
            _chars = new char[needed];
        }

        int decoded = _decoder.GetChars(bytes, _chars, flush: false);
        for (int i = 0; i < decoded; i++)
        {
            Process(_chars[i]);
        }
    }

    /// <summary>
    /// Emits whatever is still buffered as a final record — the stream ended without a line
    /// break. A dangling partial UTF-8 sequence surfaces as its replacement character rather
    /// than vanishing silently.
    /// </summary>
    public void Flush()
    {
        int decoded = _decoder.GetChars(ReadOnlySpan<byte>.Empty, _chars, flush: true);
        for (int i = 0; i < decoded; i++)
        {
            Process(_chars[i]);
        }

        if (_line.Length > 0)
        {
            emit(_line.ToString());
        }

        _line.Clear();
        _discardingOverlongLine = false;
        _pendingCarriageReturn = false;
    }

    private void Process(char c)
    {
        if (_discardingOverlongLine)
        {
            // The truncated record is already out; swallow the rest of the line. A CR ends the
            // discard too — what follows repaints from column 0, which is a fresh record here.
            if (c is '\n' or '\r')
            {
                _discardingOverlongLine = false;
            }

            return;
        }

        switch (c)
        {
            case '\n':
                emit(_line.ToString());
                _line.Clear();
                _pendingCarriageReturn = false;
                break;

            case '\r':
                // Not a record boundary by itself — "\r\n" must not emit twice. It arms an
                // overwrite: the next printable character repaints the line from column 0,
                // which is how "45%\r67%\r100%\n" becomes one "100%" record instead of a
                // record per repaint (or one record with embedded returns).
                _pendingCarriageReturn = true;
                break;

            default:
                if (_pendingCarriageReturn)
                {
                    _line.Clear();
                    _pendingCarriageReturn = false;
                }

                _line.Append(c);
                if (_line.Length >= maxLineLength)
                {
                    emit(_line.Append(TruncationMarker).ToString());
                    _line.Clear();
                    _discardingOverlongLine = true;
                }

                break;
        }
    }
}
