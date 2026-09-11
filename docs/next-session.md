# Next session — BassWave level, then touch-gesture mapping

Written 2026-09-12, at the end of the UI session. Read this first; it is the whole context.

**Goal, in order:**

1. **BassWave level writable** — a slider, `-5 … +5`. Currently reads `+5` on this unit.
2. **Touch-gesture mapping writable** — the 18-record table on the Controls page.

Both are **first transmissions of a setter that has never been sent to this hardware.**
Neither goes near the app until the Python probe has proved the encoding. That is the
discipline that has kept this project from bricking a unit, and it is not optional here.

---

## Where things stand

Everything below is **read-only today** and displayed with a note saying so:

| Feature | Getter | Setter | Setter confidence |
|---|---|---|---|
| BassWave level | `0x0124` — **confirmed** | `0x041B` | `assumed-pro3`, **never transmitted** |
| BassWave on/off | `0x010D` feature id `0x1D` — **confirmed** | `0x0403` misc-config set | `assumed-pro3`, **never transmitted** |
| Touch config | `0x0108` — **confirmed**, 18 records | `0x0401` | `assumed-pro3`, **never transmitted** |

Both setter ids are on `known.SET_ALLOWLIST` (Python) and `OpCommands.SetAllowlist` (C#),
in **Tier D** — the tier that has never been run.

Confirmed setters, for reference on what a working one looks like: `0x0400` find,
`0x0404` ANC, `0x0406` EQ preset.

---

## 1. BassWave

### What is known

`0x0124` reply body is `[status][min][max][current]`, **signed bytes**:

```
status=00  min=FB (-5)  max=05 (+5)  current=05 (+5)
```

Notification subject `0x08` pushes `[min][max][current]` on change — so a phone-side change
is observable live, and **that is the cheapest way to learn the setter's payload shape
without transmitting anything.**

`0x1D` is the BassWave *enable* flag in misc config, separate from the level.

### Plan

1. **Capture the phone doing it first.** `python -m opbuds_probe listen --mac …`, then move
   the BassWave slider on the phone one step at a time. The `0x05 0x08` notifications
   confirm the value encoding end to end, and if the phone also emits its own `0x041B`-shaped
   frame anywhere in the capture, that *is* the answer — no guessing left.
2. **Only then** add a Tier D probe: snapshot first, set `current` to a value **already
   adjacent to the present one** (e.g. `+5 → +4`), read `0x0124` back, restore in `finally`.
   Payload shape to try first, by analogy with the confirmed ANC setter, which is
   count-prefixed: `[count=0x01][value]` with `value` as a signed byte. If that is rejected
   with status `0x02` (arguments rejected — the known error shape is `02 18 0c 20 xx`), try
   the bare `[value]` form. **Two shapes, then stop and go back to the capture.** Do not
   walk the payload space.
3. Confirm by readback, not by ear.
4. Then, and only then: `OpBuds.Protocol` codec + `BudsController.SetBassWaveAsync`, and turn
   the Sound page's read-only "Level N" text into a `Slider` with `Minimum`/`Maximum` bound
   from the device's own reported `min`/`max` — never hardcoded, since the device reports them.

### What could go wrong

`0x041B` is a Buds Pro 3 id. If the Buds 4 maps that id to something else entirely, the ack
will be a rejection — but it might not be. **This is the risk the snapshot exists for.** Take
the full `get-sweep` snapshot before the first write, as Tier A/B/C did.

---

## 2. Touch gestures

### What is known

`0x0108` returns **18 records of 4 bytes**: `[side][type][gesture][action]`. The request
payload is ignored entirely. Observed live: six gestures at context `0x01`, three at `0x06`.

The Controls page already renders all 18 correctly.

### Plan

1. **Read `0x0108` and keep the exact bytes.** The rule, already written down and non-negotiable:
   **touch config is written back verbatim from the bytes read, never reconstructed.**
2. Change **one byte** — one record's `action` — in the buffer that came off the wire, and
   send the whole buffer back via `0x0401`.
3. Read `0x0108` again. Compare byte for byte. Anything that changed other than the byte you
   touched is a red flag: stop and restore.
4. Restore the original buffer in `finally`, unconditionally.
5. Verify the gesture physically — the buds must be **out of the ears** for that, and a
   double-tap is the safest one to test since it does not involve ANC.
6. Then the C# codec and a per-side × per-gesture combo-box matrix.

### The open question to resolve first

Whether `0x0401` expects the **whole 18-record table** or a **single record**. Gadgetbridge's
Oppo implementation sends a single `TouchConfig` entry, which suggests per-record. The
count-prefixed getter model used everywhere else on this device suggests the same. **Check
the capture before assuming the whole table** — sending a full table where a single record is
expected is exactly the kind of length-mismatch that produces undefined firmware behaviour.

---

## Rules that apply to both, unchanged

- **Never fuzz the `0x04xx` space.** Only allowlisted ids are ever transmitted.
- Full read-only snapshot before the first SET of a session; restored in `finally`, including
  on Ctrl+C.
- Touch config written back verbatim.
- Firmware OTA is permanently out of scope.
- **No brute-force ANC sweeps** — repeated ANC switching makes the user nauseous. Neither of
  these two features needs ANC touched at all; keep it that way.
- Gadgetbridge is AGPL-3.0: research reference only, no Java copied.

## Also outstanding

- **ANC strength dropdown never verified against live hardware.** Implemented and rendering;
  the one thing not yet seen is the combo box appearing after selecting "Noise cancellation",
  because that needs one real ANC switch. Fold it into the next live session — one switch,
  not a sweep.
- **Empty touch/misc tables on one reconnect.** Seen once: the tables came back empty where
  they had 18 rows minutes earlier. Suspected a timeout in the 8-read `RefreshAllAsync` chain
  leaving stale/empty data with no user-visible feedback. Worth a look while touching the
  Controls page anyway.
- `JsonDeviceProfile` — still deferred from Stage 2.
