using OpBuds.Core.Diagnostics;
using OpBuds.Core.Session;
using OpBuds.Core.Transport;
using OpBuds.Protocol.Commands;
using OpBuds.Protocol.Framing;
using OpBuds.Protocol.Model;
using OpBuds.Protocol.Profiles;

namespace OpBuds.Cli;

/// <summary>
/// How every feature gets proven before any XAML exists.
/// </summary>
/// <remarks>
/// Hand-rolled argument parsing on purpose: a dependency here would have to be carried by
/// the app too, and this surface is a dozen verbs that will not grow.
/// </remarks>
internal static class Program
{
    private const string DefaultMac = "12:34:56:78:9A:BC";

    private const byte BassWaveFeatureId = 0x1D;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        var mac = Option(args, "--mac") ?? DefaultMac;
        var capture = HasFlag(args, "--capture");
        var verb = args[0];

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return verb switch
            {
                "replay" => await ReplayAsync(args, cancellation.Token),
                "battery" or "info" or "anc" or "eq" or "listen" or "find" or "features" =>
                    await LiveAsync(verb, args, mac, capture, cancellation.Token),
                _ => Fail($"unknown command '{verb}'"),
            };
        }
        catch (ChannelBusyException ex)
        {
            Console.Error.WriteLine($"  {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("  cancelled");
            return 130;
        }
    }

    private static async Task<int> LiveAsync(
        string verb, string[] args, string mac, bool capture, CancellationToken cancellationToken)
    {
        var profile = Buds4Profile.Instance;
        var transport = new RawBthSocketTransport(mac, profile.ServiceUuid);

        using var log = capture
            ? ProtocolLog.Create(CaptureDirectory(), mac, $"cli-{verb}")
            : ProtocolLog.Null;

        if (capture && log.Path is not null)
        {
            Console.WriteLine($"  logging to {log.Path}");
        }

        Console.WriteLine($"  connecting to {mac} by service UUID (Windows resolves the channel)...");

        await using var session = new BudsSession(transport, profile, log);
        await session.StartAsync(cancellationToken);

        Console.WriteLine($"  {session.State.ToString().ToLowerInvariant()}");
        if (session.State == SessionState.Degraded)
        {
            Console.WriteLine("  the device has not answered yet; readings below may time out.");
        }

        var client = new BudsClient(session);

        return verb switch
        {
            "battery" => await ShowBatteryAsync(client, cancellationToken),
            "info" => await ShowInfoAsync(client, cancellationToken),
            "features" => await ShowFeaturesAsync(client, cancellationToken),
            "anc" => await AncAsync(client, args, cancellationToken),
            "eq" => await EqAsync(client, args, cancellationToken),
            "find" => await FindAsync(client, args, cancellationToken),
            "listen" => await ListenAsync(session, args, cancellationToken),
            _ => Fail($"unhandled verb '{verb}'"),
        };
    }

    private static async Task<int> ShowBatteryAsync(BudsClient client, CancellationToken cancellationToken)
    {
        var levels = await client.GetBatteryAsync(cancellationToken);
        if (levels is null)
        {
            return Fail("no battery reply");
        }

        foreach (var level in levels)
        {
            var charging = level.Charging ? " (charging)" : string.Empty;
            Console.WriteLine($"  {level.Side,-5} {level.Percent,3}%{charging}");
        }

        return 0;
    }

    private static async Task<int> ShowInfoAsync(BudsClient client, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  firmware   {await client.GetFirmwareAsync(cancellationToken) ?? "?"}");
        Console.WriteLine($"  anc        {Describe(await client.GetAncAsync(cancellationToken))}");
        Console.WriteLine($"  eq preset  {Describe(await client.GetEqPresetAsync(cancellationToken))}");

        var bass = await client.GetBassWaveAsync(cancellationToken);
        Console.WriteLine(bass is null
            ? "  basswave   ?"
            : $"  basswave   {bass.Value.Current:+#;-#;0} (range {bass.Value.Minimum}..{bass.Value.Maximum})");

        var wear = await client.GetWearAsync(cancellationToken);
        if (wear is not null)
        {
            foreach (var state in wear)
            {
                var where = state switch
                {
                    { Present: false } => "absent",
                    { InEar: true } => "in ear",
                    { InCase: true } => "in case",
                    _ => "out, not worn",
                };
                Console.WriteLine($"  wear {state.Side,-5} {where}");
            }
        }

        var battery = await client.GetBatteryAsync(cancellationToken);
        if (battery is not null)
        {
            Console.WriteLine("  battery    " + string.Join(
                " | ", battery.Select(level => $"{level.Side} {level.Percent}%")));
        }

        var peers = await client.GetConnectedDevicesAsync(cancellationToken);
        if (peers is not null)
        {
            foreach (var peer in peers)
            {
                Console.WriteLine($"  peer       {peer.MacAddress}  {peer.Name}");
            }
        }

        return 0;
    }

    private static async Task<int> ShowFeaturesAsync(BudsClient client, CancellationToken cancellationToken)
    {
        var features = await client.GetMiscFeaturesAsync(cancellationToken);
        if (features is null)
        {
            return Fail("no feature reply");
        }

        Console.WriteLine($"  {features.Count} features (paged under the 15-entry reply cap)");
        foreach (var feature in features)
        {
            var name = feature.Id == BassWaveFeatureId ? "  basswave" : string.Empty;
            Console.WriteLine($"    0x{feature.Id:X2} = {feature.Value}{name}");
        }

        return 0;
    }

    private static async Task<int> AncAsync(BudsClient client, string[] args, CancellationToken cancellationToken)
    {
        var requested = Positional(args, 1);
        if (requested is null)
        {
            Console.WriteLine($"  anc  {Describe(await client.GetAncAsync(cancellationToken))}");
            Console.WriteLine("  modes: " + string.Join(", ", Enum.GetNames<AncMode>().Select(Kebab)));
            return 0;
        }

        if (!TryParseEnum<AncMode>(requested, out var mode))
        {
            return Fail($"unknown ANC mode '{requested}'");
        }

        Console.WriteLine($"  setting {Kebab(mode.ToString())}...");
        var readback = await client.SetAncAsync(mode, cancellationToken);

        if (readback is null)
        {
            return Fail("no readback; the write may not have applied");
        }

        Console.WriteLine($"  readback {Kebab(readback.Value.ToString())}");
        return readback == mode ? 0 : Fail("readback does not match what was set");
    }

    private static async Task<int> EqAsync(BudsClient client, string[] args, CancellationToken cancellationToken)
    {
        var requested = Positional(args, 1);
        if (requested is null)
        {
            Console.WriteLine($"  eq  {Describe(await client.GetEqPresetAsync(cancellationToken))}");
            Console.WriteLine("  presets: " + string.Join(", ", Enum.GetNames<EqPreset>().Select(Kebab)));
            return 0;
        }

        if (!TryParseEnum<EqPreset>(requested, out var preset))
        {
            return Fail($"unknown preset '{requested}'");
        }

        var readback = await client.SetEqPresetAsync(preset, cancellationToken);
        if (readback is null)
        {
            return Fail("no readback; the write may not have applied");
        }

        Console.WriteLine($"  readback {Kebab(readback.Value.ToString())}");
        return readback == preset ? 0 : Fail("readback does not match what was set");
    }

    private static async Task<int> FindAsync(BudsClient client, string[] args, CancellationToken cancellationToken)
    {
        var which = Positional(args, 1) ?? "both";
        if (!TryParseEnum<FindTarget>(which, out var target))
        {
            return Fail($"unknown target '{which}' (left, right, both)");
        }

        Console.WriteLine("  *** THIS RINGS THE EARBUDS AT LOCATE VOLUME. ***");
        Console.WriteLine("  Take them out of your ears first. The tone stops on its own;");
        Console.WriteLine("  there is no stop command.");

        var accepted = await client.FindAsync(target, cancellationToken);

        // Reported honestly: the ack means accepted, not audible.
        Console.WriteLine(accepted
            ? "  accepted (an OK ack does not guarantee a sound)"
            : "  rejected");

        return accepted ? 0 : 1;
    }

    private static async Task<int> ListenAsync(BudsSession session, string[] args, CancellationToken cancellationToken)
    {
        var seconds = int.TryParse(Option(args, "--duration"), out var parsed) ? parsed : 60;
        Console.WriteLine($"  listening {seconds}s. Change a setting on the phone.");

        session.NotificationReceived += (_, frame) =>
            Console.WriteLine(
                $"  [{DateTime.Now:HH:mm:ss}] {OpCommands.Name(frame.Command),-28} " +
                $"0x{frame.Command:X4}  {Convert.ToHexString(frame.Payload.Span).ToLowerInvariant()}");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C during a listen is the normal way to stop.
        }

        return 0;
    }

    private static async Task<int> ReplayAsync(string[] args, CancellationToken cancellationToken)
    {
        var path = Positional(args, 1);
        if (path is null || !File.Exists(path))
        {
            return Fail("replay needs a path to a capture .jsonl");
        }

        // holdOpen: false — offline decoding ends when the capture ends.
        var chunks = ReplayTransport.FromCaptureFile(path, holdOpen: false);
        var reader = new FrameReader();
        var counts = new Dictionary<ushort, int>();
        var total = 0;

        await foreach (var chunk in chunks.ReceiveAsync(cancellationToken))
        {
            reader.Append(chunk.Span);
            while (reader.TryReadFrame(out var frame))
            {
                total++;
                counts[frame.Command] = counts.GetValueOrDefault(frame.Command) + 1;
            }
        }

        Console.WriteLine($"  {total} frames, {reader.DiscardedBytes} bytes discarded resynchronising");
        foreach (var (command, count) in counts.OrderByDescending(entry => entry.Value))
        {
            Console.WriteLine($"    0x{command:X4}  {count,4}  {OpCommands.Name(command)}");
        }

        return 0;
    }

    private static string CaptureDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "captures"));

    private static string Describe<T>(T? value)
        where T : struct, Enum =>
        value is null ? "?" : Kebab(value.Value.ToString());

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static bool TryParseEnum<T>(string text, out T value)
        where T : struct, Enum
    {
        var normalised = text.Replace("-", string.Empty).Replace("_", string.Empty);
        return Enum.TryParse(normalised, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static string? Positional(string[] args, int index) =>
        index < args.Length && !args[index].StartsWith('-') ? args[index] : null;

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"  {message}");
        return 1;
    }

    private static void Usage() => Console.WriteLine("""
          opbuds <command> [--mac AA:BB:CC:DD:EE:FF] [--capture]

            battery              L / R / case percentages
            info                 firmware, ANC, EQ, BassWave, wear, peers
            features             misc feature switches, paged safely
            anc [mode]           read, or set: off, transparency, adaptive,
                                 noise-cancelling-high/-moderate/-low/-auto
            eq [preset]          read, or set: balanced, serenade, bass, custom
            find [left|right|both]   rings the buds. Take them out first.
            listen [--duration N]    print notifications as they arrive
            replay <file.jsonl>      decode a capture offline, no hardware

          --capture writes a JSONL capture alongside the run.
        """);
}
