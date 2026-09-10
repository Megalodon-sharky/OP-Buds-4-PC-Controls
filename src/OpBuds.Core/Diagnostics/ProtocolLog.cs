using System.Globalization;
using System.Text.Json;

namespace OpBuds.Core.Diagnostics;

/// <summary>
/// Appends the same JSONL capture format the Python probe writes.
/// </summary>
/// <remarks>
/// <para>
/// One record class is ground truth and the other is interpretation, deliberately. An
/// <c>io</c> record holds the raw chunk exactly as the socket delivered it, never
/// frame-split, so partial-frame boundaries survive into the file. A <c>frame</c> record is
/// this code's reading of those bytes and can be regenerated offline at any time.
/// </para>
/// <para>
/// That split is what makes a capture outlive a decoder bug: when the interpretation turns
/// out to be wrong — and on this protocol it has, repeatedly — the ground truth is still
/// there to re-read.
/// </para>
/// </remarks>
public sealed class ProtocolLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly long _startTicks = Environment.TickCount64;

    private ProtocolLog(StreamWriter writer) => _writer = writer;

    public string? Path { get; private init; }

    /// <summary>A log that discards everything, so callers never need a null check.</summary>
    public static ProtocolLog Null { get; } = new(StreamWriter.Null);

    /// <summary>Opens a capture file named like the probe's: UTC stamp, MAC, subcommand.</summary>
    public static ProtocolLog Create(string directory, string macAddress, string subcommand)
    {
        Directory.CreateDirectory(directory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var mac = macAddress.Replace(":", string.Empty).ToLowerInvariant();
        var path = System.IO.Path.Combine(directory, $"{stamp}_{mac}_{subcommand}.jsonl");

        var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        var log = new ProtocolLog(writer) { Path = path };

        log.Write(new Dictionary<string, object?>
        {
            ["type"] = "meta",
            ["schema"] = 1,
            ["tool"] = "OpBuds.Core/0.1",
            ["mac"] = macAddress,
            ["subcommand"] = subcommand,
            ["transport"] = "rfcomm",
        });

        return log;
    }

    public void Io(string direction, ReadOnlySpan<byte> raw, string? note = null)
    {
        var record = new Dictionary<string, object?>
        {
            ["type"] = "io",
            ["t"] = Elapsed(),
            ["dir"] = direction,
            ["raw"] = Convert.ToHexString(raw).ToLowerInvariant(),
        };

        if (note is not null)
        {
            record["note"] = note;
        }

        Write(record);
    }

    public void Frame(ushort command, byte sequence, ReadOnlySpan<byte> payload) =>
        Write(new Dictionary<string, object?>
        {
            ["type"] = "frame",
            ["t"] = Elapsed(),
            ["command"] = $"0x{command:X4}",
            ["seq"] = sequence,
            ["payload"] = Convert.ToHexString(payload).ToLowerInvariant(),
        });

    public void Note(string message) =>
        Write(new Dictionary<string, object?>
        {
            ["type"] = "note",
            ["t"] = Elapsed(),
            ["message"] = message,
        });

    private double Elapsed() =>
        Math.Round((Environment.TickCount64 - _startTicks) / 1000.0, 4);

    private void Write(Dictionary<string, object?> record)
    {
        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(record, Options));
        }
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_writer, StreamWriter.Null))
        {
            _writer.Dispose();
        }
    }
}
