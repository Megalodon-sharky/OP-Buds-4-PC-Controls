# Capturing the Buds 4 protocol from an Android phone

This is Stage 0 of the project and the single highest-value step in it. Nobody has
publicly reverse-engineered the OnePlus Buds 4, so every opcode we currently have is
extrapolated from the Buds Pro 3 and from Oppo/Realme models. One good capture replaces
all of that guesswork with fact.

Capture device for this project: **OnePlus 13R** (`11:22:33:44:55:66`).

---

## 1. Enable the snoop log

1. Settings -> About device -> tap **Build number** 7 times to unlock Developer options.
2. Settings -> System -> Developer options -> enable **Bluetooth HCI snoop log**.
   - On OxygenOS this may be a three-way selector; choose **Enabled**, not "Disabled".
3. **Toggle Bluetooth off and back on.** The log file is only opened at stack startup --
   skipping this step is the most common reason a capture comes back empty.

---

## 2. Record the session

Connect the Buds 4 to the phone, then open their settings page (OxygenOS surfaces it
under the Bluetooth device entry; HeyMelody works too).

Work through the list below. Two rules matter more than anything else:

- **One control at a time.** Change a single setting, then stop.
- **Pause ~5 seconds between each.** The gaps are what let you line frames up with actions.

Keep a written log as you go. A phone stopwatch running alongside makes this easy.
Format does not matter as long as it has elapsed time and what you touched:

```
00:05  ANC -> Off
00:12  ANC -> Transparency
00:19  ANC -> Noise cancellation
00:26  ANC level -> Low
```

**This tap log is what turns hex into meaning.** Without it the capture is a wall of
undifferentiated bytes and most of its value is lost.

### Sequence to record

| # | Action | Why it matters |
|---|--------|----------------|
| 1 | ANC -> Off | Establishes the ANC mode encoding |
| 2 | ANC -> Transparency | same |
| 3 | ANC -> Noise cancellation | same |
| 4 | ANC level -> Low, then Moderate, then High | Derives the level bitmask |
| 5 | ANC -> Adaptive | Buds 4 has this; Buds Pro 3 encoding may not apply |
| 6 | EQ preset -> Balanced, then Serenade, then Bass | **Derives the 3-preset ID map** -- known to differ from Buds Pro 3's 5 presets |
| 7 | Custom EQ -> move one band, apply | Reveals the custom EQ payload layout |
| 8 | BassWave -> slide to minimum, then maximum | Confirms range encoding and feature id |
| 9 | Touch gesture -> reassign one gesture on one earbud | Reveals the touch config matrix |
| 10 | Find my earbuds -> start, then stop | Simplest command; good sanity anchor |
| 11 | 3D Audio -> on, then off | Likely a feature-switch toggle with an unknown id |
| 12 | Golden Sound -> run the full flow start to finish | Multi-step; the hardest target |
| 13 | Game mode -> on, then off | |
| 14 | LHDC / codec -> toggle if exposed | |
| 15 | Dual connection -> toggle if exposed | |
| 16 | Take earbuds out, put them back in | Wear detection notifications |
| 17 | Put both in the case, close, reopen | Connection teardown behaviour |

Steps 1-10 are the ones the app most needs. 11-15 are what make full HeyMelody parity
possible. If you run out of patience, stop after 10 and we can capture the rest later --
a partial capture is still enormously useful.

---

## 3. Pull the log

With USB debugging enabled and the phone connected:

```bash
adb bugreport buds-capture.zip
```

Then extract `FS/data/misc/bluetooth/logs/btsnoop_hci.log` from the zip.

Direct pull is faster but blocked on many recent builds:

```bash
adb pull /data/misc/bluetooth/logs/btsnoop_hci.log
```

If neither works, check the path shown in Developer options -- some OxygenOS builds
write to `/sdcard/btsnoop_hci.log`.

Drop the resulting `.log` and your tap log into `captures/` in this repo.

---

## 4. Reading it

Open in Wireshark. Useful display filters:

| Filter | Shows |
|--------|-------|
| `btrfcomm` | RFCOMM frames -- the control channel lives here |
| `btspp` | Serial-port-profile payloads |
| `bthci_cmd or bthci_evt` | Connection setup, useful for finding the channel number |
| `btrfcomm.channel == N` | Once you know the channel, isolates it |

To read a conversation as a byte stream rather than frame-by-frame, right-click a data
frame -> **Follow** -> the TCP-like stream option.

**What to look for:** frames beginning with `0xAA`. The byte after it is a length that
counts everything following it, so a frame is `len + 2` bytes total. Byte 4 is the command
id, byte 5 the message type (`01` request, `81` reply, `04` set, `84` ack, `02`/`05` push).

Findings get transcribed into `docs/protocol.md` with a confidence rating per row.

---

## 5. Turn it off afterwards

The snoop log records **all** Bluetooth traffic, including from other paired devices, and
it keeps growing. Disable **Bluetooth HCI snoop log** in Developer options once you are
done capturing, and toggle Bluetooth off/on to close the file.
