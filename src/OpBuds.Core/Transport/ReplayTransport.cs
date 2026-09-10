using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpBuds.Core.Transport;

/// <summary>
/// Replays the <c>rx</c> chunks of a JSONL capture. No hardware.
/// </summary>
/// <remarks>
/// <para>
/// Chunks are replayed <b>exactly as they were recorded</b>, split where the socket split
/// them. That is the point: real wire boundaries are free fuzz coverage for the frame
/// reader, and they are more adversarial than anything worth hand-writing — a 309-byte
/// frame arriving in five pieces with a notification interleaved is not a case anyone
/// invents.
/// </para>
/// <para>
/// Sends are recorded rather than matched. Replay proves the reader, not the writer.
/// </para>
/// </remarks>
public sealed class ReplayTransport : IBudsTransport
{
    private readonly IReadOnlyList<byte[]> _chunks;
    private readonly List<byte[]> _sent = [];
    private readonly TimeSpan _chunkDelay;

    private readonly bool _holdOpen;

    /// <param name="receiveChunks">Wire chunks to replay, in order.</param>
    /// <param name="chunkDelay">Optional pause between chunks, to mimic real timing.</param>
    /// <param name="holdOpen">
    /// When true (the default) the receive loop stays open after the last chunk, so a session
    /// does not mistake the end of a capture for a link drop. Offline decoding wants false:
    /// there is nothing more coming and the caller should not have to cancel to find out.
    /// </param>
    public ReplayTransport(IEnumerable<byte[]> receiveChunks, TimeSpan? chunkDelay = null, bool holdOpen = true)
    {
        _chunks = [.. receiveChunks];
        _chunkDelay = chunkDelay ?? TimeSpan.Zero;
        _holdOpen = holdOpen;
    }

    public bool IsConnected { get; private set; }

    public string Description => $"replay ({_chunks.Count} chunks)";

    /// <summary>Everything written during the replay, in order.</summary>
    public IReadOnlyList<byte[]> Sent => _sent;

    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected;

    /// <summary>Loads the <c>rx</c> side of a probe or app capture.</summary>
    public static ReplayTransport FromCaptureFile(
        string path, TimeSpan? chunkDelay = null, bool holdOpen = true) =>
        new(ParseChunks(File.ReadLines(path), "rx"), chunkDelay, holdOpen);

    /// <summary>Loads from JSONL lines already in memory. Used by tests.</summary>
    public static ReplayTransport FromJsonLines(
        IEnumerable<string> lines, TimeSpan? chunkDelay = null, bool holdOpen = true) =>
        new(ParseChunks(lines, "rx"), chunkDelay, holdOpen);

    private static List<byte[]> ParseChunks(IEnumerable<string> lines, string direction)
    {
        var chunks = new List<byte[]>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "io")
            {
                continue;
            }

            if (!root.TryGetProperty("dir", out var dir) || dir.GetString() != direction)
            {
                continue;
            }

            if (root.TryGetProperty("raw", out var raw) && raw.GetString() is { } hex)
            {
                chunks.Add(Convert.FromHexString(hex));
            }
        }

        return chunks;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        _sent.Add(data.ToArray());
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in _chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_chunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
            }

            yield return chunk;
        }

        if (!_holdOpen)
        {
            yield break;
        }

        // Hold the loop open so a replay does not look like a link drop; the session's
        // lifetime token ends it.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (IsConnected)
        {
            IsConnected = false;
            Disconnected?.Invoke(this, new TransportDisconnectedEventArgs(DisconnectReason.Requested));
        }

        return ValueTask.CompletedTask;
    }
}
