# OP Buds 4 PC Controls

Control **OnePlus Buds 4** from Windows — ANC, EQ, battery, touch gestures and the rest of
what HeyMelody shows on a phone and nothing shows on a PC.

A tray app, a CLI, and a protocol library, talking the earbuds' own OPO control protocol
over Bluetooth Classic RFCOMM. No phone involved.

<!-- Screenshots: drop dashboard.png / sound.png in docs/img and uncomment.
![Dashboard](docs/img/dashboard.png)
-->

---

## What works

| | |
|---|---|
| **Battery** | Left, right and case, live, with charging state |
| **Noise control** | Off · Transparency · Adaptive · Noise cancellation (auto / low / moderate / high) — set from the window or the tray menu |
| **Equaliser** | Balanced · Serenade · Bass · saved custom profile |
| **BassWave** | Level slider, −5 to +5 — the range is read from the earbuds, not hardcoded |
| **Touch gestures** | Double press, triple press and press-and-hold, per bud: play/pause, previous, next, voice assistant, noise control, or nothing |
| **Find my earbuds** | Per bud or both |
| **Wear state** | In ear / out / in case, per bud |
| **Device info** | Firmware, MAC, connected peers |
| **Tray** | Battery percentage drawn into the icon, ANC menu, close-to-tray, start with Windows |
| **Developer page** | Live annotated hex of every frame, raw frame sender, export as JSONL |

Every write is confirmed by reading the setting back. A gesture change goes further: the
whole gesture table is read back and must match the old one with exactly one byte different.

**Read-only for now:** the misc feature switches (including BassWave's own on/off) and
custom EQ bands. Their write formats have not been verified on this hardware, so they are
shown with a note rather than as dead controls. See [Safety](#safety).

**Not supported, and won't be:**

- **3D Audio** — measured and refuted. Toggling it on the phone while polling all fifty
  responding commands produced zero changes and no notification. It is a phone-side spatial
  upmix; there is no bud-side setting to control.
- **Hi-res / LHDC** — real, stored on the buds, and useless here: on Windows the A2DP codec
  lives in the Microsoft Bluetooth audio stack (SBC and AAC only), not in the radio. Toggling
  it also drops the control connection. Shown as a read-only status row.
- **Firmware updates** — permanently out of scope. A failed update bricks the hardware with
  no recovery path.

---

## Install

Windows 10 2004+ / Windows 11, [.NET 8 desktop runtime](https://dotnet.microsoft.com/download/dotnet/8.0),
and the earbuds already paired.

```powershell
dotnet publish src\OpBuds.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

That produces one file:

```
src\OpBuds.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\OpBuds.exe
```

Double-click it. Pin it to Start, or turn on **Start with Windows** in Settings, and it
launches straight to the tray.

The exe is unsigned, so SmartScreen will warn once: **More info → Run anyway**.

**First run:** open Settings and put your earbuds' Bluetooth address in — the app starts
with a placeholder (`12:34:56:78:9A:BC`) and will not connect until you do. To find the
address, run `python -m opbuds_probe devices` from `tools\probe`: it lists every paired
device with its MAC.

---

## Using it

Left-click the tray icon to show or hide the window; right-click for battery, noise control,
Find and Exit. Closing the window hides it to the tray — the app keeps the control channel
and keeps polling.

Six pages: Dashboard, Sound, Controls, Device, Settings, Developer.

### CLI

The same stack without a window. Useful for scripting and for checking the app against an
independent reading.

```powershell
dotnet run --project src\OpBuds.Cli -- info    --mac AA:BB:CC:DD:EE:FF
dotnet run --project src\OpBuds.Cli -- battery --mac AA:BB:CC:DD:EE:FF
dotnet run --project src\OpBuds.Cli -- anc     --mac AA:BB:CC:DD:EE:FF --mode adaptive
dotnet run --project src\OpBuds.Cli -- eq      --mac AA:BB:CC:DD:EE:FF --preset bass
dotnet run --project src\OpBuds.Cli -- listen  --mac AA:BB:CC:DD:EE:FF
dotnet run --project src\OpBuds.Cli -- replay  captures\some-run.jsonl
```

`replay` needs no hardware — it re-decodes a saved capture offline.

---

## How it works

The earbuds advertise the OPO control service `0000079a-d102-11e1-9b23-00025b00a5a5` in
their SDP record. Windows resolves the RFCOMM channel from that UUID, so the channel number
is never guessed. Frames look like this:

```
AA <len varint> 00 00 <cmdId> <msgType> <seq> <plenLo> <plenHi> [payload]
```

There is no checksum. Resync is done by scanning for `0xAA` and rejecting any frame whose
declared payload length disagrees with its total length.

**Two corrections to every published implementation of this protocol**, both measured here:

1. **`len` is a base-128 varint, not a single byte.** Single-byte readings work only while
   payloads stay under 128. This device sends a 299-byte payload on `0x011F`, and a
   single-byte reading mis-frames the stream and silently discards it — along with every
   notification that follows it in the same buffer.
2. **Byte 4 is the command id and byte 5 is the message type** — not one little-endian
   16-bit command. The 16-bit model breaks on REGISTER, and request/reply correlation has to
   key on `(type, id, seq)` because the 16-bit values collide.

One firmware quirk worth knowing if you build on this: **`0x010D` (misc config) caps its
reply at 32 payload bytes — exactly 15 entries — and reports the truncated count as if it
were real.** Ask for 17 ids and you lose the top 2 with no error and no flag. Every request
here is paged at 15.

The full constant table, with a confidence column per row, is in
[`docs/protocol.md`](docs/protocol.md).

---

## Repo layout

```
src\OpBuds.Protocol   framing, codecs, device profiles   (net8.0, no Windows deps)
src\OpBuds.Core       transport, session, supervisor, battery poller
src\OpBuds.Cli        opbuds.exe
src\OpBuds.App        the WPF tray app
tests\                111 tests, none need hardware
tools\probe\          the Python research harness that mapped the protocol
docs\protocol.md      the wire format and every measured constant
captures\             JSONL captures (gitignored — they contain MACs)
```

### Build

```powershell
dotnet build src\OpBuds.sln -c Release -warnaserror
dotnet test  src\OpBuds.sln
```

### The Python probe

Stdlib only, no pip install, Windows only. It is kept because it re-earns its keep on every
firmware update and is the independent oracle when the C# stack misbehaves.

```powershell
cd tools\probe
python -m opbuds_probe devices
python -m opbuds_probe get-sweep --mac AA:BB:CC:DD:EE:FF
python -m opbuds_probe listen    --mac AA:BB:CC:DD:EE:FF
```

Every run writes a JSONL capture to `captures\`, in the same format the C# side reads.

---

## Safety

This talks to firmware that has no published specification. The rules below are enforced in
code, not just documented, and they are why nothing here has ever bricked a unit:

- **The `0x04xx` setter space is never fuzzed.** Only commands on an explicit allowlist are
  ever transmitted as writes. An unknown setter in that space could be a factory reset or a
  firmware-update entry point.
- A **full read-only snapshot** is taken before the first write of a session and restored in
  a `finally` block, including on Ctrl+C.
- Touch configuration is **never rebuilt from a model**. A change sends one record copied
  from the bytes read with only its action replaced, then the whole table is read back and
  compared byte for byte.
- Every new setter is proven first by the Python probe: a snapshot, one small change, a read
  of every command id to catch side effects, and an automatic restore.
- Anything whose opcode is still a guess stays **read-only** until it has been confirmed on
  hardware.

If the earbuds stop responding: both in the case, lid closed ten seconds, reopen. For a full
reset, hold the case button for about ten seconds.

---

## Troubleshooting

**"Control channel busy"** — something else holds the vendor channel. Close the earbud
settings page on your phone.

**"In case"** — not an error. The app drops to a slow poll and waits.

**The window is blank** — correct layout, no pixels. On a laptop this usually means the lid
is closed: with the internal panel off there is no render target and WPF's hardware pipeline
draws nothing. Open the lid. If it happens with the lid open, a driver reset has wedged the
pipeline; set `"softwareRendering": true` in `%AppData%\OpBudsControls\settings.json` and
restart.

**Nothing in the notification area** — the icon is probably in the overflow. Click the `^`
chevron and drag it out.

**Doesn't start with Windows** — *Start with Windows* registers a Task Scheduler sign-in task
named `OpBudsControls`, which starts the app in the tray about ten seconds after you sign in.
It deliberately does not use the registry Run key: on the machine this was built on, Explorer
skipped that entry at every sign-in with no visible reason. Because of that it won't show up
in Task Manager's Startup tab — look in Task Scheduler instead. Turning the setting off and
on again recreates the task.

Settings and logs live in `%AppData%\OpBudsControls\`.

---

## Credits

The protocol was not invented here. Three prior efforts made this possible:

- [`nic0manz/oneplus_buds3_pro_python`](https://github.com/nic0manz/oneplus_buds3_pro_python)
  and [`oneplus_buds3_pro_dotnet`](https://github.com/nic0manz/oneplus_buds3_pro_dotnet) —
  OnePlus Buds Pro 3 over RFCOMM. Most of the payload layouts started here.
- [`AasheeshLikePanner/cracked-oneplus-buds`](https://github.com/AasheeshLikePanner/cracked-oneplus-buds)
  and its [write-up](https://aasheesh.vercel.app/blog/oneplus-buds) — the same framing over
  BLE on a Nord Buds 3 Pro.
- [Gadgetbridge](https://codeberg.org/Freeyourgadget/Gadgetbridge) — a production
  implementation for Oppo Enco and Realme earbuds, and the source of the command codes for
  touch config, find-device and misc settings.

**Gadgetbridge is AGPL-3.0.** It was read to learn the wire format and **no source from it,
or from any project above, has been copied into this repository.** Everything here is written
from [`docs/protocol.md`](docs/protocol.md). Please keep it that way — see
[`NOTICE.md`](NOTICE.md).

Not affiliated with OnePlus, Oppo or Google. OnePlus, HeyMelody, BassWave and Golden Sound
are trademarks of their respective owners.

---

## Licence

[MIT](LICENSE). Research sources and their licences are in [`NOTICE.md`](NOTICE.md).
