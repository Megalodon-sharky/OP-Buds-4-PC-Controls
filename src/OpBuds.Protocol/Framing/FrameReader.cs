namespace OpBuds.Protocol.Framing;

/// <summary>
/// Reassembles frames from arbitrary wire chunks.
/// </summary>
/// <remarks>
/// <para>
/// RFCOMM delivers whatever the stack happens to have; a single reply routinely arrives split
/// across several reads, and several notifications routinely arrive in one. Chunk boundaries
/// carry no protocol meaning, so this class must produce an identical frame sequence whether
/// fed one byte at a time or in a single blob. The tests assert exactly that.
/// </para>
/// <para>
/// Resynchronisation is not theoretical here: the standard HELLO is ten bytes but declares a
/// nine-byte frame, so the trailing <c>0x12</c> is real garbage on the wire from the very
/// first exchange of every session.
/// </para>
/// </remarks>
public sealed class FrameReader
{
    private readonly int _maxBufferBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public FrameReader(int maxBufferBytes = 16 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferBytes, FrameCodec.MaxPayloadLength);
        _maxBufferBytes = maxBufferBytes;
        _buffer = new byte[Math.Min(1024, maxBufferBytes)];
    }

    /// <summary>Bytes discarded while hunting for a frame boundary. Should stay at zero.</summary>
    public int DiscardedBytes { get; private set; }

    /// <summary>Number of buffered bytes not yet consumed.</summary>
    public int Buffered => _end - _start;

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        EnsureCapacity(chunk.Length);
        chunk.CopyTo(_buffer.AsSpan(_end));
        _end += chunk.Length;
    }

    /// <summary>
    /// Decodes the next frame, discarding leading garbage as needed.
    /// </summary>
    /// <returns>False when more data is required.</returns>
    /// <exception cref="FrameDesynchronizedException">
    /// The buffer filled to its cap without yielding a frame.
    /// </exception>
    public bool TryReadFrame(out OpFrame frame)
    {
        while (true)
        {
            var window = _buffer.AsSpan(_start, _end - _start);

            switch (FrameCodec.TryDecode(window, out frame, out var consumed))
            {
                case DecodeStatus.Ok:
                    _start += consumed;
                    Compact();
                    return true;

                case DecodeStatus.NeedMoreData:
                    if (Buffered >= _maxBufferBytes)
                    {
                        throw new FrameDesynchronizedException(
                            $"{Buffered} bytes buffered without completing a frame; " +
                            "the stream is desynchronised.");
                    }

                    Compact();
                    return false;

                default:
                    // Skip one byte and hunt for the next plausible start. Skipping to the
                    // *next* 0xAA rather than one byte would be faster but would step over a
                    // 0xAA that appears inside a payload prefix, so advance conservatively.
                    if (window.IsEmpty)
                    {
                        return false;
                    }

                    _start++;
                    DiscardedBytes++;
                    break;
            }
        }
    }

    /// <summary>Reads every frame currently available.</summary>
    public List<OpFrame> Drain()
    {
        var frames = new List<OpFrame>();
        while (TryReadFrame(out var frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Drops all buffered data. Used after a transport-level reconnect.</summary>
    public void Reset()
    {
        _start = 0;
        _end = 0;
    }

    private void EnsureCapacity(int additional)
    {
        if (_end + additional <= _buffer.Length)
        {
            return;
        }

        Compact();

        if (_end + additional <= _buffer.Length)
        {
            return;
        }

        var needed = _end + additional;
        var size = _buffer.Length;
        while (size < needed)
        {
            size *= 2;
        }

        // Growing past the cap is allowed only so the cap check in TryReadFrame can fire with
        // a real message; a runaway stream is bounded there, not here.
        Array.Resize(ref _buffer, Math.Min(size, Math.Max(needed, _maxBufferBytes)));
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        if (_start == _end)
        {
            _start = 0;
            _end = 0;
            return;
        }

        _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
        _end -= _start;
        _start = 0;
    }
}

public sealed class FrameDesynchronizedException(string message) : Exception(message);
