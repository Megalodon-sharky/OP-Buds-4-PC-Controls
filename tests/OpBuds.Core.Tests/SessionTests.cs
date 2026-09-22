using OpBuds.Core.Session;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Codecs;
using OpBuds.Protocol.Commands;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;
using Shouldly;
using Xunit;

namespace OpBuds.Core.Tests;

public class BthEndPointTests
{
    [Fact]
    public void SerialisesTheThirtyByteStructure()
    {
        var uuid = new Guid("0000079a-d102-11e1-9b23-00025b00a5a5");
        var endpoint = new BthEndPoint(BthEndPoint.ParseAddress("12:34:56:78:9A:BC"), uuid);

        var address = endpoint.Serialize();
        address.Size.ShouldBe(30);

        // AF_BTH, little-endian, in the two bytes SocketAddress owns.
        address[0].ShouldBe((byte)32);
        address[1].ShouldBe((byte)0);

        // btAddr is a ULONGLONG, so the MAC reads back reversed with two zero bytes.
        var expected = new byte[] { 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12, 0x00, 0x00 };
        for (var i = 0; i < expected.Length; i++)
        {
            address[2 + i].ShouldBe(expected[i], $"btAddr byte {i}");
        }

        // The GUID goes in Windows' native layout, which is what Guid.ToByteArray gives.
        var guid = uuid.ToByteArray();
        for (var i = 0; i < guid.Length; i++)
        {
            address[10 + i].ShouldBe(guid[i], $"guid byte {i}");
        }

        // Port zero is what makes Windows resolve the channel over SDP.
        for (var i = 0; i < 4; i++)
        {
            address[26 + i].ShouldBe((byte)0);
        }
    }

    [Fact]
    public void RoundTripsThroughCreate()
    {
        var uuid = Guid.NewGuid();
        var original = new BthEndPoint(BthEndPoint.ParseAddress("12:34:56:78:9A:BC"), uuid, 15);

        var revived = (BthEndPoint)original.Create(original.Serialize());

        revived.Address.ShouldBe(original.Address);
        revived.ServiceClassId.ShouldBe(uuid);
        revived.Port.ShouldBe(15u);
    }

    [Theory]
    [InlineData("12:34:56:78:9A:BC")]
    [InlineData("123456789ABC")]
    [InlineData("12-34-56-78-9A-BC")]
    public void AcceptsTheUsualMacSpellings(string mac) =>
        BthEndPoint.ParseAddress(mac).ShouldBe(0x123456789ABCUL);

    [Fact]
    public void RejectsNonsense() =>
        Should.Throw<FormatException>(() => BthEndPoint.ParseAddress("88:92:CC"));

    [Fact]
    public void FormatsBackToTheOriginal() =>
        BthEndPoint.FormatAddress(0x123456789ABCUL).ShouldBe("12:34:56:78:9A:BC");
}

public class BudsSessionTests
{
    private static readonly byte[] BatteryPayload = [0x00, 0x02, 0x01, 0x5A, 0x02, 0x5A];

    private static ScriptedTransport Transport(int? splitEvery = null) => new() { SplitEvery = splitEvery };

    [Fact]
    public async Task SendsHelloVerbatimBeforeAnythingElse()
    {
        var transport = Transport().Respond(MessageTypes.Request, OpCommands.BatteryGet, MessageTypes.Reply, BatteryPayload);
        await using var session = new BudsSession(transport, Buds4Profile.Instance);

        await session.StartAsync(CancellationToken.None);

        transport.Sent.Count.ShouldBeGreaterThan(0);
        transport.Sent[0].ShouldBe(Buds4Profile.Instance.Handshake.Hello.ToArray());
    }

    [Fact]
    public async Task CorrelatesARequestWithItsReply()
    {
        var transport = Transport().Respond(MessageTypes.Request, OpCommands.BatteryGet, MessageTypes.Reply, BatteryPayload);
        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var reply = await session.RequestAsync(OpCommands.BatteryGet, ReadOnlyMemory<byte>.Empty);

        reply.ShouldNotBeNull();
        reply.Value.Command.ShouldBe((ushort)0x8106);
        reply.Value.Payload.ToArray().ShouldBe(BatteryPayload);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task CorrelatesEvenWhenTheReplyArrivesInPieces(int chunkSize)
    {
        // A real reply routinely arrives split. Reassembly must not change the outcome.
        var transport = Transport(chunkSize)
            .Respond(MessageTypes.Request, OpCommands.BatteryGet, MessageTypes.Reply, BatteryPayload);

        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var reply = await session.RequestAsync(OpCommands.BatteryGet, ReadOnlyMemory<byte>.Empty);

        reply.ShouldNotBeNull();
        reply.Value.Payload.ToArray().ShouldBe(BatteryPayload);
    }

    [Fact]
    public async Task TimesOutToNullRatherThanThrowing()
    {
        // A missing reply is ordinary on a Bluetooth link, not exceptional.
        var transport = Transport().Respond(MessageTypes.Request, OpCommands.BatteryGet, MessageTypes.Reply, BatteryPayload);
        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var reply = await session.RequestAsync(
            OpCommands.EqPresetGet, ReadOnlyMemory<byte>.Empty, timeout: TimeSpan.FromMilliseconds(150));

        reply.ShouldBeNull();
    }

    [Fact]
    public async Task RoutesNotificationsToTheEventNotThePendingMap()
    {
        var transport = Transport().Respond(MessageTypes.Request, OpCommands.BatteryGet, MessageTypes.Reply, BatteryPayload);
        await using var session = new BudsSession(transport, Buds4Profile.Instance);

        var received = new TaskCompletionSource<OpFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NotificationReceived += (_, frame) => received.TrySetResult(frame);

        await session.StartAsync(CancellationToken.None);
        transport.Push(0x04, MessageTypes.Notify, [0x03, 0x01, 0x01, 0x00, 0x08]);

        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        frame.IsNotification.ShouldBeTrue();
        frame.Command.ShouldBe((ushort)0x0204);
    }

    [Fact]
    public async Task RefusesASetCommandThatIsNotOnTheAllowlist()
    {
        // The 0x04xx space is never fuzzed: an unknown setter could be factory reset or DFU.
        var transport = Transport();
        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var before = transport.Sent.Count;

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.SetAsync(0x7F, ReadOnlyMemory<byte>.Empty));

        // Nothing reached the transport.
        transport.Sent.Count.ShouldBe(before);
    }

    [Fact]
    public async Task RefusesTwoInFlightRequestsForOneCommand()
    {
        var transport = Transport();
        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var first = session.RequestAsync(
            OpCommands.BatteryGet, ReadOnlyMemory<byte>.Empty, timeout: TimeSpan.FromMilliseconds(400));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await session.RequestAsync(
                OpCommands.BatteryGet, ReadOnlyMemory<byte>.Empty, timeout: TimeSpan.FromMilliseconds(100)));

        (await first).ShouldBeNull();
    }
}

public class BudsClientTests
{
    [Fact]
    public async Task ParsesBatteryEndToEnd()
    {
        var transport = new ScriptedTransport()
            .Respond(MessageTypes.Request, OpCommands.BatteryGet, MessageTypes.Reply, [0x00, 0x02, 0x01, 0x5A, 0x02, 0x5A]);

        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var levels = await new BudsClient(session).GetBatteryAsync();

        levels.ShouldNotBeNull();
        levels.Count.ShouldBe(2);
        levels[0].Percent.ShouldBe(90);
    }

    [Fact]
    public async Task RefusesAnAncModeTheProfileDoesNotSupport()
    {
        var transport = new ScriptedTransport();
        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var client = new BudsClient(session);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await client.SetAncAsync((AncMode)0x1000));
    }

    [Fact]
    public async Task SetsBassWaveWithTheDeviceBoundsAndReportsTheReadback()
    {
        var transport = new ScriptedTransport()
            .Respond(MessageTypes.Set, OpCommands.BassWaveSet, MessageTypes.SetAck, [0x00])
            .Respond(MessageTypes.Request, OpCommands.BassWaveGet, MessageTypes.Reply, [0x00, 0xFB, 0x05, 0x04]);

        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var readback = await new BudsClient(session).SetBassWaveAsync(new BassWaveState(-5, 5, 5), 4);

        readback.ShouldBe(new BassWaveState(-5, 5, 4));
        SentPayload(transport, MessageTypes.Set, OpCommands.BassWaveSet).ShouldBe(new byte[] { 0xFB, 0x05, 0x04 });
    }

    [Fact]
    public async Task ReportsNothingWhenTheDeviceRejectsABassWaveLevel()
    {
        var transport = new ScriptedTransport()
            .Respond(MessageTypes.Set, OpCommands.BassWaveSet, MessageTypes.SetAck, [0x01]);

        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        (await new BudsClient(session).SetBassWaveAsync(new BassWaveState(-5, 5, 5), 4)).ShouldBeNull();
    }

    [Fact]
    public async Task SetsOneTouchRecordAndReturnsTheWholeTableReadBack()
    {
        TouchCodec.TryParse(new byte[] { 0x00, 0x02, 0x01, 0x01, 0x02, 0x05, 0x02, 0x01, 0x02, 0x06 }, out var before)
            .ShouldBeTrue();

        var transport = new ScriptedTransport()
            .Respond(MessageTypes.Set, OpCommands.TouchConfigSet, MessageTypes.SetAck, [0x00])
            .Respond(MessageTypes.Request, OpCommands.TouchConfigGet, MessageTypes.Reply,
                [0x00, 0x02, 0x01, 0x01, 0x02, 0x05, 0x02, 0x01, 0x02, 0x05]);

        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var after = await new BudsClient(session).SetTouchActionAsync(before, before.Bindings[1], 0x05);

        after.ShouldNotBeNull();
        TouchCodec.IsExactlyOneEdit(before, after, before.Bindings[1], 0x05).ShouldBeTrue();
        SentPayload(transport, MessageTypes.Set, OpCommands.TouchConfigSet)
            .ShouldBe(new byte[] { 0x01, 0x02, 0x01, 0x02, 0x05 });
    }

    private static byte[] SentPayload(ScriptedTransport transport, byte messageType, byte commandId) =>
        transport.Sent
            .Select(data => FrameCodec.TryDecode(data, out var frame, out _) == DecodeStatus.Ok ? frame : (OpFrame?)null)
            .Single(frame => frame is { } f && f.MessageType == messageType && f.CommandId == commandId)!
            .Value.Payload.ToArray();

    [Fact]
    public async Task PagesTheMiscReadSoNoIdIsSilentlyLost()
    {
        // Fifteen ids come back on the first page and two on the second. A single
        // whole-space request would return a well-formed reply missing the top two.
        var first = MiscPage(
            0x0F, [0x02, 0x04, 0x05, 0x06, 0x0B, 0x0C, 0x0D, 0x11, 0x12, 0x14, 0x16, 0x17, 0x18, 0x19, 0x1B]);
        var second = MiscPage(0x02, [0x1D, 0x1E]);

        var transport = new SequencedMiscTransport(first, second);
        await using var session = new BudsSession(transport, Buds4Profile.Instance);
        await session.StartAsync(CancellationToken.None);

        var features = await new BudsClient(session).GetMiscFeaturesAsync();

        features.ShouldNotBeNull();
        features.Count.ShouldBe(17);
        features.Select(f => f.Id).ShouldContain((byte)0x1D);
        features.Select(f => f.Id).ShouldContain((byte)0x1E);
    }

    private static byte[] MiscPage(byte count, byte[] ids)
    {
        var payload = new List<byte> { 0x00, count };
        foreach (var id in ids)
        {
            payload.Add(id);
            payload.Add(0x01);
        }

        return [.. payload];
    }
}

/// <summary>Answers successive misc requests with successive pages.</summary>
internal sealed class SequencedMiscTransport(params byte[][] pages) : IBudsTransport
{
    private readonly ScriptedTransport _inner = new();
    private int _next;

    public bool IsConnected => _inner.IsConnected;

    public string Description => "sequenced misc";

    public event EventHandler<TransportDisconnectedEventArgs>? Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (FrameCodec.TryDecode(data.Span, out var frame, out _) == DecodeStatus.Ok
            && frame.CommandId == OpCommands.MiscConfigGet
            && frame.MessageType == MessageTypes.Request
            && _next < pages.Length)
        {
            _inner.Respond(MessageTypes.Request, OpCommands.MiscConfigGet, MessageTypes.Reply, pages[_next++]);
        }

        return _inner.SendAsync(data, cancellationToken);
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _inner.ReceiveAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

public class ReplayTransportTests
{
    [Fact]
    public async Task ReplaysOnlyReceiveChunksAndStopsWhenAsked()
    {
        var frame = FrameCodec.Encode(
            new OpFrame(0x06, MessageTypes.Reply, 0x00, new byte[] { 0x00, 0x01, 0x01, 0x5A }));
        var hex = Convert.ToHexString(frame).ToLowerInvariant();

        string[] lines =
        [
            """{"type":"meta","schema":1,"tool":"test"}""",
            """{"type":"io","t":0.1,"dir":"tx","raw":"aa070000000123000012"}""",
            $$"""{"type":"io","t":0.2,"dir":"rx","raw":"{{hex}}"}""",
        ];

        var transport = ReplayTransport.FromJsonLines(lines, holdOpen: false);

        var chunks = new List<ReadOnlyMemory<byte>>();
        await foreach (var chunk in transport.ReceiveAsync(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        // The tx line is ignored; only rx is replayed.
        chunks.Count.ShouldBe(1);
        chunks[0].ToArray().ShouldBe(frame);
    }
}
