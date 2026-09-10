# OP Buds 4 PC Controls

Control OnePlus Buds 4 settings from Windows -- ANC, EQ, battery and the rest of
what the HeyMelody app exposes on a phone but nothing exposes on a PC.

**Status: early. The protocol is still being reverse-engineered.**

---

## Where the project is

| Stage | State |
|---|---|
| 0 -- Capture the protocol from an Android phone | not started (needs a phone + Wireshark) |
| 1 -- Python probe harness | **working, and talking to real hardware** |
| 2 -- C# protocol library | not started |
| 3 -- Transport, session, CLI | not started |
| 4 -- WPF app | not started |

**The protocol works, and most of it is now decoded.** Connecting to the OPO service by
UUID succeeds, the handshake gets a reply, and every known read command returns real
data. It also works while the earbuds are simultaneously connected to a phone.

Confirmed against hardware: the wear-detection flag bits, the ANC mode masks, the
battery list including the charging case, the connected-device table, and the 18-entry
touch-gesture table. The full map is in [`docs/protocol.md`](docs/protocol.md).

One correction worth knowing if you have read the reference implementations: the frame
length field is a **base-128 varint**, not a single byte. Every published implementation
treats it as one byte, which works only because their payloads happen to stay under 128.
This device sends a 299-byte payload on command `0x011F`, and a single-byte reading
mis-frames the stream and silently discards it -- along with every notification that
follows it in the same buffer.

What is already established, with evidence, lives in [`docs/protocol.md`](docs/protocol.md).

The most important thing confirmed so far: **the Buds 4 advertises the OPO control
service** `0000079a-d102-11e1-9b23-00025b00a5a5` in its SDP record. That is the same
service Gadgetbridge drives on Oppo and Realme earbuds, which makes this project very
likely to work.

---

## Using the probe harness

Python 3.10+, no dependencies, Windows only.

```powershell
cd tools\probe
python -m opbuds_probe devices
```

| Command | What it does |
|---|---|
| `devices` | List paired Bluetooth devices |
| `sdp` | Show which RFCOMM services the earbuds advertise, and the channel if known |
| `scan` | Brute-force which RFCOMM channels accept a connection |
| `handshake` | Try HELLO + REGISTER and log every byte that comes back |
| `get-sweep` | Send every known read request and dump the replies |
| `listen` | Stay connected and print notifications as you change settings on the phone |
| `set-probe` | Tiered writes, with a snapshot taken first and restored afterwards |
| `decode` | Re-parse a saved capture offline, no hardware needed |

Every run writes a JSONL capture to `captures/`. Those files are the input to the
C# work, so keep them.

The earbuds must be **out of the case and connected** for anything beyond
`devices` and `sdp` to work.

### Safety

`set-probe` changes real settings. It will not run without
`--i-understand-the-risk`, it takes a full read-only snapshot first, and each tier
restores what it changed in a `finally` block -- including on Ctrl+C.

The `0x04xx` write space is **never fuzzed**. Only commands on an explicit
allowlist are ever transmitted as writes, because an unknown setter in that space
could be a factory reset or a firmware-update entry point.

If the earbuds stop responding: put both in the case, close the lid for ten
seconds, reopen. For a full reset, hold the case button for about ten seconds.

**Firmware updates are permanently out of scope for this project.** A failed
update bricks the hardware with no recovery path.

---

## Credits

The protocol was not invented here. Three prior efforts made this possible:

- [`nic0manz/oneplus_buds3_pro_python`](https://github.com/nic0manz/oneplus_buds3_pro_python)
  and [`oneplus_buds3_pro_dotnet`](https://github.com/nic0manz/oneplus_buds3_pro_dotnet)
  -- OnePlus Buds Pro 3 over RFCOMM. Most of the payload layouts come from here.
- [`AasheeshLikePanner/cracked-oneplus-buds`](https://github.com/AasheeshLikePanner/cracked-oneplus-buds)
  and its [write-up](https://aasheesh.vercel.app/blog/oneplus-buds) -- the same
  framing over BLE on a Nord Buds 3 Pro.
- [Gadgetbridge](https://codeberg.org/Freeyourgadget/Gadgetbridge) -- a production
  implementation for Oppo Enco and Realme earbuds, and the source of the command
  codes for touch config, find-device and misc settings.

See [`NOTICE.md`](NOTICE.md) for licensing.

Not affiliated with OnePlus, Oppo or Google.
