using OpBuds.Protocol.Codecs;
using OpBuds.Protocol.Commands;
using OpBuds.Protocol.Model;

namespace OpBuds.Core.Session;

/// <summary>
/// A typed API over <see cref="BudsSession"/>.
/// </summary>
/// <remarks>
/// Every method here returns null on timeout rather than throwing, because a missing reply
/// is an ordinary event on a Bluetooth link — a bud goes in the case mid-poll and that is
/// not exceptional. Writes are the exception: an invalid argument throws, because the device
/// will not reject it.
/// </remarks>
public sealed class BudsClient(BudsSession session)
{
    public BudsSession Session => session;

    public async Task<IReadOnlyList<BatteryLevel>?> GetBatteryAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.BatteryGet, BatteryCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && BatteryCodec.TryParse(reply.Value.Payload, out var levels) ? levels : null;
    }

    public async Task<IReadOnlyList<WearState>?> GetWearAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.WearGet, WearCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && WearCodec.TryParse(reply.Value.Payload, out var states) ? states : null;
    }

    public async Task<AncMode?> GetAncAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.AncGet, AncCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && AncCodec.TryParse(reply.Value.Payload, out var mode) ? mode : null;
    }

    /// <summary>
    /// Sets ANC and confirms by reading it back.
    /// </summary>
    /// <remarks>
    /// The readback is not belt-and-braces: an OK ack has been observed on commands that did
    /// nothing at all, so the ack alone cannot be the success signal.
    /// </remarks>
    public async Task<AncMode?> SetAncAsync(AncMode mode, CancellationToken cancellationToken = default)
    {
        if (!session.Profile.SupportedAncModes.Contains(mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, $"{session.Profile.Name} does not support this ANC mode");
        }

        var ack = await session.SetAsync(OpCommands.AncSet, AncCodec.BuildSet(mode), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (ack is null || !ack.Value.Ok)
        {
            return null;
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return await GetAncAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EqPreset?> GetEqPresetAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.EqPresetGet, EqPresetCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && EqPresetCodec.TryParse(reply.Value.Payload, out var preset) ? preset : null;
    }

    public async Task<EqPreset?> SetEqPresetAsync(EqPreset preset, CancellationToken cancellationToken = default)
    {
        if (!session.Profile.SupportedEqPresets.Contains(preset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset), preset, $"{session.Profile.Name} does not expose this preset");
        }

        var ack = await session.SetAsync(OpCommands.EqPresetSet, EqPresetCodec.BuildSet(preset), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (ack is null || !ack.Value.Ok)
        {
            return null;
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return await GetEqPresetAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BassWaveState?> GetBassWaveAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.BassWaveGet, BassWaveCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && BassWaveCodec.TryParse(reply.Value.Payload, out var state) ? state : null;
    }

    /// <summary>Sets the BassWave level and confirms by reading it back.</summary>
    /// <remarks>
    /// <paramref name="bounds"/> is a reading from the device: its minimum and maximum are
    /// echoed in the write and bound the level, so the range is always the device's own.
    /// </remarks>
    public async Task<BassWaveState?> SetBassWaveAsync(
        BassWaveState bounds,
        int level,
        CancellationToken cancellationToken = default)
    {
        var ack = await session.SetAsync(OpCommands.BassWaveSet, BassWaveCodec.BuildSet(bounds, level), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (ack is null || !ack.Value.Ok)
        {
            return null;
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return await GetBassWaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-points one gesture and returns the whole table as read back afterwards.
    /// </summary>
    /// <remarks>
    /// Returns the full table rather than a yes/no so the caller can check, byte for byte,
    /// that the one record changed and nothing else did.
    /// </remarks>
    public async Task<TouchConfiguration?> SetTouchActionAsync(
        TouchConfiguration current,
        TouchBinding binding,
        byte action,
        CancellationToken cancellationToken = default)
    {
        var ack = await session.SetAsync(OpCommands.TouchConfigSet, TouchCodec.BuildSet(current, binding, action), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (ack is null || !ack.Value.Ok)
        {
            return null;
        }

        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return await GetTouchConfigAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CustomEqProfile>?> GetCustomEqAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.CustomEqGet, CustomEqCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && CustomEqCodec.TryParse(reply.Value.Payload, out var profiles) ? profiles : null;
    }

    public async Task<string?> GetFirmwareAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.FirmwareGet, FirmwareCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && FirmwareCodec.TryParse(reply.Value.Payload, out var firmware) ? firmware : null;
    }

    public async Task<TouchConfiguration?> GetTouchConfigAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.TouchConfigGet, TouchCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && TouchCodec.TryParse(reply.Value.Payload, out var config) ? config : null;
    }

    public async Task<IReadOnlyList<ConnectedDevice>?> GetConnectedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await session.RequestAsync(OpCommands.ConnectedDevicesGet, ConnectedDevicesCodec.BuildRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return reply is not null && ConnectedDevicesCodec.TryParse(reply.Value.Payload, out var devices) ? devices : null;
    }

    /// <summary>Fire-and-forget. The tone self-terminates; there is no stop opcode.</summary>
    public async Task<bool> FindAsync(FindTarget target, CancellationToken cancellationToken = default)
    {
        var ack = await session.SetAsync(OpCommands.FindDeviceSet, FindDeviceCodec.BuildSet(target), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Deliberately reports only that the command was accepted. Whether anything was
        // audible is not knowable from the protocol: OK acks have produced silence.
        return ack is not null && ack.Value.Ok;
    }

    /// <summary>
    /// Reads every misc feature, paging under the reply cap.
    /// </summary>
    /// <remarks>
    /// One request per page. A single whole-space request would come back well-formed,
    /// status OK, and quietly missing its highest ids.
    /// </remarks>
    public async Task<IReadOnlyList<MiscFeature>?> GetMiscFeaturesAsync(CancellationToken cancellationToken = default)
    {
        var features = new List<MiscFeature>();

        foreach (var page in MiscConfigCodec.Pages(session.Profile.MiscFeatureIds))
        {
            var reply = await session.RequestAsync(OpCommands.MiscConfigGet, MiscConfigCodec.BuildRequest(page), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (reply is null || !MiscConfigCodec.TryParse(reply.Value.Payload, page, out var result))
            {
                return null;
            }

            if (result.SuspectedTruncation)
            {
                throw new InvalidOperationException(
                    $"0x010D truncated a {page.Length}-id request; this should be impossible " +
                    "under the paging limit and means the cap differs from what was measured.");
            }

            features.AddRange(result.Features);
        }

        return features;
    }
}
