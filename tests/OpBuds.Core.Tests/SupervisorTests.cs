using System.Collections.Concurrent;
using OpBuds.Core.Session;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;
using Shouldly;
using Xunit;

namespace OpBuds.Core.Tests;

/// <summary>
/// The backoff ladder, tested in milliseconds.
/// </summary>
/// <remarks>
/// Every delay is injected, so these run at full speed while exercising the real loop. The
/// alternative — a supervisor that sleeps for real — would make the thirty-second rung
/// untestable, which is exactly the rung most likely to be wrong.
/// </remarks>
public class ConnectionSupervisorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public void TheLadderIsOneTwoFourEightFifteenThirty() =>
        ConnectionSupervisor.Backoff.Select(rung => rung.TotalSeconds)
            .ShouldBe([1, 2, 4, 8, 15, 30]);

    [Fact]
    public async Task FailedConnectsClimbFromOneSecondAndCapAtThirty()
    {
        var delays = new ConcurrentQueue<TimeSpan>();
        var parked = new TaskCompletionSource();

        await using var supervisor = new ConnectionSupervisor(
            () => throw new IOException("no radio"),
            Buds4Profile.Instance,
            delay: (duration, token) =>
            {
                delays.Enqueue(duration);

                // Park once the ladder has been walked, rather than spinning at full speed.
                if (delays.Count < 8)
                {
                    return Task.CompletedTask;
                }

                parked.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            },
            random: new Random(1));

        supervisor.Start();
        await parked.Task.WaitAsync(Patience);

        var walked = delays.Take(8).Select(delay => delay.TotalSeconds).ToArray();

        // Jitter is ±20%, so each rung is asserted as a band rather than a value.
        ShouldBeAbout(walked[0], 1);
        ShouldBeAbout(walked[1], 2);
        ShouldBeAbout(walked[2], 4);
        ShouldBeAbout(walked[3], 8);
        ShouldBeAbout(walked[4], 15);
        ShouldBeAbout(walked[5], 30);
        ShouldBeAbout(walked[6], 30);
        ShouldBeAbout(walked[7], 30);
    }

    [Fact]
    public async Task JitterActuallyVaries()
    {
        var delays = new ConcurrentQueue<TimeSpan>();
        var parked = new TaskCompletionSource();

        await using var supervisor = new ConnectionSupervisor(
            () => throw new IOException("no radio"),
            Buds4Profile.Instance,
            delay: (duration, token) =>
            {
                delays.Enqueue(duration);

                if (delays.Count < 12)
                {
                    return Task.CompletedTask;
                }

                parked.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            },
            random: new Random(7));

        supervisor.Start();
        await parked.Task.WaitAsync(Patience);

        // The capped rungs must not all be identical, or many clients would resynchronise
        // onto the same retry instant — which is the entire point of the jitter.
        delays.Skip(5).Take(6).Select(delay => delay.Ticks).Distinct().Count().ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task ABusyChannelSkipsToTheTopRungAndNamesTheRemedy()
    {
        var delays = new ConcurrentQueue<TimeSpan>();
        var statuses = new ConcurrentQueue<SupervisorStatus>();
        var parked = new TaskCompletionSource();

        await using var supervisor = new ConnectionSupervisor(
            () => throw new ChannelBusyException("the earbuds' control channel refused the connection."),
            Buds4Profile.Instance,
            delay: (duration, token) =>
            {
                delays.Enqueue(duration);
                parked.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });

        supervisor.StatusChanged += (_, status) => statuses.Enqueue(status);
        supervisor.Start();
        await parked.Task.WaitAsync(Patience);

        // Never retry-storm a channel something else legitimately holds.
        ShouldBeAbout(delays.First().TotalSeconds, 30);

        var busy = statuses.Single(status => status.ChannelBusy);
        busy.Summary.ShouldContain("phone");
        busy.IsQuiet.ShouldBeFalse();
    }

    [Fact]
    public async Task ACaseCycleIsQuietAndWaitsTheLongRung()
    {
        var delays = new ConcurrentQueue<TimeSpan>();
        var statuses = new ConcurrentQueue<SupervisorStatus>();
        var connected = new TaskCompletionSource();
        var parked = new TaskCompletionSource();

        var transport = new ScriptedTransport();

        // Any frame at all proves the device is listening, which is what promotes the
        // session out of Degraded.
        transport.Push(0x06, MessageTypes.Notify, [0x00]);

        await using var supervisor = new ConnectionSupervisor(
            () => transport,
            Buds4Profile.Instance,
            delay: (duration, token) =>
            {
                delays.Enqueue(duration);
                parked.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });

        supervisor.StatusChanged += (_, status) => statuses.Enqueue(status);
        supervisor.Connected += (_, _) => connected.TrySetResult();

        // What the poller would have set from the last wear reading.
        supervisor.LastKnownInCase = true;

        supervisor.Start();
        await connected.Task.WaitAsync(Patience);

        await transport.DisposeAsync();
        await parked.Task.WaitAsync(Patience);

        ShouldBeAbout(delays.First().TotalSeconds, 30);

        var inCase = statuses.Last(status => status.State == SessionState.Disconnected);
        inCase.InCase.ShouldBeTrue();
        inCase.Summary.ShouldBe("In case");
        inCase.IsQuiet.ShouldBeTrue();
    }

    [Fact]
    public async Task AGoodConnectHandsOutAUsableClient()
    {
        BudsClient? handed = null;
        var connected = new TaskCompletionSource();

        var transport = new ScriptedTransport();
        transport.Push(0x06, MessageTypes.Notify, [0x00]);

        await using var supervisor = new ConnectionSupervisor(
            () => transport,
            Buds4Profile.Instance,
            delay: (_, token) => Task.Delay(Timeout.Infinite, token));

        supervisor.Connected += (_, client) =>
        {
            handed = client;
            connected.TrySetResult();
        };

        supervisor.Start();
        await connected.Task.WaitAsync(Patience);

        handed.ShouldNotBeNull();
        supervisor.Client.ShouldBeSameAs(handed);
        supervisor.Status.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public async Task ReconnectNowCutsTheWaitShort()
    {
        var entered = new SemaphoreSlim(0);

        await using var supervisor = new ConnectionSupervisor(
            () => throw new IOException("no radio"),
            Buds4Profile.Instance,
            delay: async (_, token) =>
            {
                entered.Release();
                await Task.Delay(Timeout.Infinite, token);
            });

        supervisor.Start();
        (await entered.WaitAsync(Patience)).ShouldBeTrue();

        // Without this the supervisor would sit out the whole rung; resume-from-sleep
        // depends on it.
        supervisor.RequestReconnectNow();

        // Entering the wait a second time means another attempt ran in between.
        (await entered.WaitAsync(Patience)).ShouldBeTrue();
    }

    private static void ShouldBeAbout(double seconds, double rung) =>
        seconds.ShouldBeInRange(rung * 0.8, rung * 1.2);
}

public class BatteryPollerTests
{
    [Fact]
    public void TheTiersAreFiveThirtyAndOneHundredTwentySeconds()
    {
        BatteryPoller.Intervals[PollTier.WindowVisible].ShouldBe(TimeSpan.FromSeconds(5));
        BatteryPoller.Intervals[PollTier.TrayOnly].ShouldBe(TimeSpan.FromSeconds(30));
        BatteryPoller.Intervals[PollTier.Idle].ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task ForcingAReadIgnoresTheTier()
    {
        var calls = 0;

        await using var poller = new BatteryPoller(
            _ =>
            {
                calls++;
                return Task.FromResult<DeviceReadout?>(Sample(80));
            },
            PollTier.Paused);

        (await poller.ForceAsync()).ShouldBeTrue();

        calls.ShouldBe(1);
        poller.Latest.Battery.Single().Percent.ShouldBe(80);
    }

    [Fact]
    public async Task ASecondPollIsDroppedRatherThanQueued()
    {
        var gate = new TaskCompletionSource();
        var starts = 0;

        await using var poller = new BatteryPoller(async _ =>
        {
            Interlocked.Increment(ref starts);
            await gate.Task;
            return Sample(50);
        });

        var first = poller.ForceAsync();

        // Queueing this would turn one slow reply into a backlog of requests for a value
        // that is about to be superseded anyway.
        (await poller.ForceAsync()).ShouldBeFalse();

        gate.SetResult();
        (await first).ShouldBeTrue();
        starts.ShouldBe(1);
    }

    [Fact]
    public async Task ALostReadLeavesTheLastGoodReadingAlone()
    {
        var answer = (DeviceReadout?)Sample(64);

        await using var poller = new BatteryPoller(_ => Task.FromResult(answer));

        (await poller.ForceAsync()).ShouldBeTrue();
        poller.Latest.Battery.Single().Percent.ShouldBe(64);

        answer = null;

        // A timed-out read is not evidence of a flat battery.
        (await poller.ForceAsync()).ShouldBeFalse();
        poller.Latest.Battery.Single().Percent.ShouldBe(64);
    }

    [Fact]
    public async Task ChangingTheTierRetimesTheRunningTimer()
    {
        await using var poller = new BatteryPoller(
            _ => Task.FromResult<DeviceReadout?>(Sample(10)),
            PollTier.Idle);

        poller.Tier.ShouldBe(PollTier.Idle);

        poller.Tier = PollTier.WindowVisible;
        poller.Tier.ShouldBe(PollTier.WindowVisible);
    }

    [Fact]
    public void AllInCaseIgnoresTheCaseItselfAndAbsentBuds()
    {
        // Present (0x04) without out-of-case (0x01) is a bud sitting in the case.
        var stowed = new DeviceReadout(
            [],
            [
                new WearState(BudSide.Left, 0x04),
                new WearState(BudSide.Right, 0x04),
                new WearState(BudSide.Case, 0x01),
            ],
            DateTimeOffset.Now);

        stowed.AllInCase.ShouldBeTrue();

        var worn = new DeviceReadout(
            [],
            [
                new WearState(BudSide.Left, 0x07),
                new WearState(BudSide.Right, 0x04),
            ],
            DateTimeOffset.Now);

        worn.AllInCase.ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyReadoutIsNotEvidenceOfBeingInTheCase() =>
        DeviceReadout.Empty.AllInCase.ShouldBeFalse();

    private static DeviceReadout Sample(int percent) =>
        new([new BatteryLevel(BudSide.Left, percent, false)], [], DateTimeOffset.Now);
}
