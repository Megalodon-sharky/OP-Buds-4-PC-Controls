using OpBuds.Protocol.Codecs;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;
using Shouldly;
using Xunit;

namespace OpBuds.Protocol.Tests;

public class AncCodecTests
{
    [Fact]
    public void ParsesAdaptive()
    {
        AncCodec.TryParse(Fixtures.AncAdaptive, out var mode).ShouldBeTrue();
        mode.ShouldBe(AncMode.Adaptive);
    }

    [Fact]
    public void ParsesTransparency()
    {
        AncCodec.TryParse(Fixtures.AncTransparency, out var mode).ShouldBeTrue();
        mode.ShouldBe(AncMode.Transparency);
    }

    [Fact]
    public void SetValueIsSixteenBitLittleEndian()
    {
        // Adaptive lives in the high byte. A three-byte payload cannot express it, which is
        // why it looked like a missing feature for an entire session.
        AncCodec.BuildSet(AncMode.Adaptive).ShouldBe(Fixtures.Hex("01 01 00 08"));
        AncCodec.BuildSet(AncMode.NoiseCancellingLow).ShouldBe(Fixtures.Hex("01 01 40 00"));
        AncCodec.BuildSet(AncMode.Off).ShouldBe(Fixtures.Hex("01 01 08 00"));
    }

    [Fact]
    public void SetRoundTripsThroughTheParser()
    {
        foreach (var mode in Enum.GetValues<AncMode>())
        {
            var set = AncCodec.BuildSet(mode);

            // The readback echoes the same 16-bit value behind a status byte.
            var reply = new byte[] { 0x00, 0x01, AncCodec.FieldId, set[2], set[3] };
            AncCodec.TryParse(reply, out var parsed).ShouldBeTrue(mode.ToString());
            parsed.ShouldBe(mode);
        }
    }

    [Fact]
    public void RefusesAnUnmodelledMask() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AncCodec.BuildSet((AncMode)0x1000));

    [Fact]
    public void ErrorShapeIsNotParsedAsState() =>
        AncCodec.TryParse(Fixtures.ErrorShape, out _).ShouldBeFalse();

    [Fact]
    public void RequestAsksForTheOnlyValidField() =>
        AncCodec.BuildRequest().ShouldBe(Fixtures.Hex("01 01"));
}

public class SoundCodecTests
{
    [Fact]
    public void ParsesEqPreset()
    {
        EqPresetCodec.TryParse(Fixtures.EqPresetBass, out var preset).ShouldBeTrue();
        preset.ShouldBe(EqPreset.Bass);
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x05)]
    [InlineData(0x07)]
    public void RefusesPresetsTheDeviceWouldSilentlyAccept(byte raw)
    {
        // The firmware acks these, notifies on them, and echoes them back. Since the device
        // will not validate, this layer must.
        Should.Throw<ArgumentOutOfRangeException>(() => EqPresetCodec.BuildSet((EqPreset)raw));
    }

    [Fact]
    public void EncodesEveryModelledPreset()
    {
        foreach (var preset in Enum.GetValues<EqPreset>())
        {
            EqPresetCodec.BuildSet(preset).ShouldBe(new[] { (byte)preset });
        }
    }

    [Fact]
    public void ParsesSignedBassWaveBounds()
    {
        BassWaveCodec.TryParse(Fixtures.BassWave, out var state).ShouldBeTrue();
        state.Minimum.ShouldBe(-5);
        state.Maximum.ShouldBe(5);
        state.Current.ShouldBe(5);
    }

    [Fact]
    public void BassWaveSetEchoesTheDeviceBoundsThenTheLevel()
    {
        // The getter's reply minus its status byte, the rule every confirmed setter follows.
        BassWaveCodec.TryParse(Fixtures.BassWave, out var state).ShouldBeTrue();

        BassWaveCodec.BuildSet(state, 4).ShouldBe(Fixtures.Hex("fb 05 04"));
        BassWaveCodec.BuildSet(state, 0).ShouldBe(Fixtures.Hex("fb 05 00"));
        BassWaveCodec.BuildSet(state, -2).ShouldBe(Fixtures.Hex("fb 05 fe"));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(-6)]
    public void BassWaveSetRefusesALevelOutsideTheReportedRange(int level)
    {
        BassWaveCodec.TryParse(Fixtures.BassWave, out var state).ShouldBeTrue();
        Should.Throw<ArgumentOutOfRangeException>(() => BassWaveCodec.BuildSet(state, level));
    }

    [Fact]
    public void ParsesEmptyCustomEqList()
    {
        CustomEqCodec.TryParse(Fixtures.CustomEqEmpty, out var profiles).ShouldBeTrue();
        profiles.ShouldBeEmpty();
    }

    [Fact]
    public void ParsesCustomEqBands()
    {
        CustomEqCodec.TryParse(Fixtures.CustomEqOneProfile, out var profiles).ShouldBeTrue();

        var profile = profiles.ShouldHaveSingleItem();
        profile.Name.ShouldBe("Custom1");
        profile.MinimumGainDb.ShouldBe(-6);
        profile.MaximumGainDb.ShouldBe(6);
        profile.Bands.Select(band => band.FrequencyHz).ShouldBe(CustomEqCodec.BandFrequenciesHz);
        profile.Bands.Select(band => band.GainDb).ShouldBe(new[] { 6, 6, 0, 0, -6, -6 });
    }
}

public class DeviceCodecTests
{
    [Fact]
    public void ParsesBattery()
    {
        BatteryCodec.TryParse(Fixtures.BatteryBothNinety, out var levels).ShouldBeTrue();

        levels.Count.ShouldBe(2);
        levels[0].Side.ShouldBe(BudSide.Left);
        levels[0].Percent.ShouldBe(90);
        levels[0].Charging.ShouldBeFalse();
        levels[1].Side.ShouldBe(BudSide.Right);
        levels[1].Percent.ShouldBe(90);
    }

    [Fact]
    public void MasksTheChargingBitOutOfThePercentage()
    {
        BatteryCodec.TryParse(Fixtures.Hex("00 01 01 da"), out var levels).ShouldBeTrue();
        levels[0].Percent.ShouldBe(90);
        levels[0].Charging.ShouldBeTrue();
    }

    [Fact]
    public void ParsesWearFlags()
    {
        WearCodec.TryParse(Fixtures.WearBothInEar, out var states).ShouldBeTrue();

        states.Count.ShouldBe(3);
        states[0].InEar.ShouldBeTrue();
        states[0].OutOfCase.ShouldBeTrue();
        states[0].Present.ShouldBeTrue();

        var caseState = states[2];
        caseState.Side.ShouldBe(BudSide.Case);
        caseState.Present.ShouldBeTrue();
        caseState.InCase.ShouldBeTrue();
        caseState.InEar.ShouldBeFalse();
    }

    [Fact]
    public void InHandIsDistinctFromInEar()
    {
        WearCodec.TryParse(Fixtures.Hex("00 01 01 05"), out var states).ShouldBeTrue();
        states[0].OutOfCase.ShouldBeTrue();
        states[0].InEar.ShouldBeFalse();
    }

    [Fact]
    public void ParsesFirmwareAsAscii()
    {
        FirmwareCodec.TryParse(Fixtures.Firmware, out var firmware).ShouldBeTrue();
        firmware.ShouldBe("1,2,138,2,2,138,3,1,01,3,2,105");
    }

    [Fact]
    public void ReversesTheConnectedDeviceMac()
    {
        ConnectedDevicesCodec.TryParse(Fixtures.ConnectedDevices, out var devices).ShouldBeTrue();

        var device = devices.ShouldHaveSingleItem();
        device.MacAddress.ShouldBe("0A:1B:2C:3D:4E:5F");
        device.Name.ShouldBe("DESKPC");
    }

    [Fact]
    public void FindTargetsAreCountPrefixedIdLists()
    {
        FindDeviceCodec.BuildSet(FindTarget.Left).ShouldBe(Fixtures.Hex("01 01"));
        FindDeviceCodec.BuildSet(FindTarget.Right).ShouldBe(Fixtures.Hex("01 02"));
        FindDeviceCodec.BuildSet(FindTarget.Both).ShouldBe(Fixtures.Hex("02 01 02"));
    }
}

public class TouchCodecTests
{
    [Fact]
    public void ParsesBindings()
    {
        TouchCodec.TryParse(Fixtures.TouchConfig, out var configuration).ShouldBeTrue();

        configuration.Bindings.Count.ShouldBe(3);
        configuration.Bindings[0].Side.ShouldBe(BudSide.Left);
        configuration.Bindings[0].Action.ShouldBe((byte)0x05);
        configuration.Bindings[1].Side.ShouldBe(BudSide.Right);
        configuration.Bindings[1].Action.ShouldBe((byte)0x06);
    }

    [Fact]
    public void SetIsOneReadRecordBehindACount()
    {
        // Right double press, re-pointed from next (06) to previous (05). Side, context and
        // gesture are copied from what was read; only the action is new.
        TouchCodec.TryParse(Fixtures.TouchConfig, out var configuration).ShouldBeTrue();

        TouchCodec.BuildSet(configuration, configuration.Bindings[1], 0x05)
            .ShouldBe(Fixtures.Hex("01 02 01 02 05"));
    }

    [Fact]
    public void RefusesToInventARecord()
    {
        TouchCodec.TryParse(Fixtures.TouchConfig, out var configuration).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => TouchCodec.BuildSet(
            configuration, new TouchBinding(BudSide.Left, 0x09, 0x09, 0x01), 0x01));
    }

    [Fact]
    public void AcceptsAReadBackDifferingInExactlyTheEditedByte()
    {
        TouchCodec.TryParse(Fixtures.TouchConfig, out var before).ShouldBeTrue();
        TouchCodec.TryParse(Fixtures.Hex("00 03 01 01 02 05 02 01 02 05 01 06 02 1d"), out var after)
            .ShouldBeTrue();

        TouchCodec.IsExactlyOneEdit(before, after, before.Bindings[1], 0x05).ShouldBeTrue();
    }

    [Fact]
    public void RejectsAReadBackWhereAnotherGestureAlsoMoved()
    {
        // The edit landed, but the call-context record changed too.
        TouchCodec.TryParse(Fixtures.TouchConfig, out var before).ShouldBeTrue();
        TouchCodec.TryParse(Fixtures.Hex("00 03 01 01 02 05 02 01 02 05 01 06 02 00"), out var after)
            .ShouldBeTrue();

        TouchCodec.IsExactlyOneEdit(before, after, before.Bindings[1], 0x05).ShouldBeFalse();
    }

    [Fact]
    public void RejectsAReadBackWhereTheEditDidNotLand()
    {
        TouchCodec.TryParse(Fixtures.TouchConfig, out var before).ShouldBeTrue();
        TouchCodec.IsExactlyOneEdit(before, before, before.Bindings[1], 0x05).ShouldBeFalse();
    }
}

public class MiscConfigCodecTests
{
    [Fact]
    public void DetectsTheSilentTruncation()
    {
        // The reply is well-formed, its status is OK, and its count says fifteen. Nothing in
        // the payload reveals that 0x1E was dropped -- only comparing against the request does.
        MiscConfigCodec.TryParse(Fixtures.MiscTruncated, Fixtures.MiscTruncatedRequest, out var result)
            .ShouldBeTrue();

        result.Features.Count.ShouldBe(MiscConfigCodec.MaxIdsPerReply);
        result.MissingIds.ShouldBe(new byte[] { 0x1E });
        result.SuspectedTruncation.ShouldBeTrue();
        Fixtures.MiscTruncated.Length.ShouldBe(MiscConfigCodec.ReplyPayloadCap);
    }

    [Fact]
    public void TheDroppedIdsAreSupportedAfterAll()
    {
        // Asking for only the two highest ids returns both, enabled. They were never absent.
        MiscConfigCodec.TryParse(Fixtures.MiscTwoIds, [0x1D, 0x1E], out var result).ShouldBeTrue();

        result.Features.Count.ShouldBe(2);
        result.MissingIds.ShouldBeEmpty();
        result.SuspectedTruncation.ShouldBeFalse();
        result.Features.ShouldAllBe(feature => feature.Enabled);
    }

    [Fact]
    public void RefusesAnOverlongRequest()
    {
        var tooMany = MiscConfigCodec.KnownIds;
        tooMany.Length.ShouldBeGreaterThan(MiscConfigCodec.MaxIdsPerReply);

        Should.Throw<ArgumentException>(() => MiscConfigCodec.BuildRequest(tooMany));
    }

    [Fact]
    public void PagesTheKnownIdsUnderTheCap()
    {
        var pages = MiscConfigCodec.Pages();

        pages.Count.ShouldBe(2);
        pages[0].Length.ShouldBe(15);
        pages[1].Length.ShouldBe(2);
        pages.SelectMany(page => page).ShouldBe(MiscConfigCodec.KnownIds);
        pages.ShouldAllBe(page => page.Length <= MiscConfigCodec.MaxIdsPerReply);
    }

    [Fact]
    public void EveryPageProducesAValidRequest()
    {
        foreach (var page in MiscConfigCodec.Pages())
        {
            var request = MiscConfigCodec.BuildRequest(page);
            request[0].ShouldBe((byte)page.Length);
            request.Length.ShouldBe(page.Length + 1);
        }
    }
}

public class ProfileTests
{
    [Fact]
    public void Buds4ExposesAllSevenAncStates()
    {
        var profile = Buds4Profile.Instance;

        profile.SupportedAncModes.Count.ShouldBe(7);
        profile.SupportedAncModes.ShouldContain(AncMode.Adaptive);
        profile.SupportedAncModes.ShouldContain(AncMode.NoiseCancellingAuto);
    }

    [Fact]
    public void Buds4EqMapDiffersFromThePro3()
    {
        Buds4Profile.Instance.SupportedEqPresets.ShouldContain(EqPreset.Custom);
        BudsPro3Profile.Instance.SupportedEqPresets.ShouldNotContain(EqPreset.Custom);
    }

    [Fact]
    public void MiscPagingLimitMatchesTheMeasuredCap() =>
        Buds4Profile.Instance.MiscMaxIdsPerRequest.ShouldBe(MiscConfigCodec.MaxIdsPerReply);

    [Fact]
    public void HelloIsReplayedVerbatimIncludingItsStrayTrailer()
    {
        // Ten bytes for a nine-byte frame. Unexplained, identical across three independent
        // implementations, and it works.
        Buds4Profile.Instance.Handshake.Hello.ToArray().ShouldBe(Fixtures.Hello);
    }

    [Fact]
    public void SetAllowlistBoundsWhatCanEverBeTransmitted()
    {
        var allowlist = Buds4Profile.Instance.SetAllowlist;

        allowlist.ShouldContain(Commands.OpCommands.AncSet);
        allowlist.ShouldContain(Commands.OpCommands.EqPresetSet);
        allowlist.ShouldNotContain((byte)0x02);
        allowlist.ShouldNotContain((byte)0xFF);
    }

    [Fact]
    public void RefutedCapabilitiesAreRecordedRatherThanForgotten()
    {
        FeatureSet.Excluded.Keys.ShouldContain("SpatialAudio");
        FeatureSet.Excluded.Keys.ShouldContain("FirmwareUpdate");
        Buds4Profile.Instance.DefaultFeatures.Has(BudsCapability.BassWave).ShouldBeTrue();
    }

    [Fact]
    public void SequenceAllocatorsDifferByProfile()
    {
        var rolling = Buds4Profile.Instance.CreateSequenceAllocator();
        rolling.Next(0x06).ShouldNotBe(rolling.Next(0x06));

        var fixedAllocator = BudsPro3Profile.Instance.CreateSequenceAllocator();
        fixedAllocator.Next(Commands.OpCommands.BatteryGet).ShouldBe((byte)0x25);
        fixedAllocator.Next(Commands.OpCommands.BatteryGet).ShouldBe((byte)0x25);
    }
}
