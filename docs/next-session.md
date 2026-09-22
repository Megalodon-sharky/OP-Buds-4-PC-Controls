# Next session

Updated 2026-09-23. The previous version of this file planned BassWave level and touch-gesture
mapping. **Both are done**: confirmed on hardware, shipped in the app, verified on the wire
from the app itself. Details are in [`protocol.md`](protocol.md) under *Writing*.

## How they were landed — reuse this for the next setter

1. **Predict the payload before sending anything.** Every confirmed setter on this hardware
   takes its getter's reply minus the status byte. That rule predicted both payloads, and a
   second source agreed each time (the Buds Pro 3 tool for BassWave, Gadgetbridge for touch).
2. **Probe tier in `tools/probe`** (`set-probe --tier BASS|TOUCH`): full snapshot, one
   *small* change (one BassWave step, one gesture swapped to a value its twin already held),
   readback, a read of every responding command id before and after to catch collateral
   changes, automatic restore. At most two payload shapes, then stop.
3. Only then the codec, client, controller and UI — and the app verifies every write by
   readback too.

Both setters worked on the first shape.

## Still open

- **ANC strength dropdown, live.** Implemented and rendering; still not seen with a real
  switch, because that needs one ANC change. One switch, not a sweep: repeated ANC switching
  makes the user nauseous.
- **BassWave on/off** is misc feature `0x1D`, written with `0x0403`, which has never been
  sent. The Buds Pro 3 sends `[featureId][value]`, the rule predicts `[count][featureId][value]`.
  They disagree, so this needs its own probe tier before any UI depends on it. Until then the
  Sound page says when the switch is off rather than offering a slider that silently does
  nothing.
- **Custom EQ upload.** The read side is fully decoded; the rule predicts the write layout.
  A new probe tier, same discipline.
- **Empty touch/misc tables on one reconnect.** Seen once. Suspected: a timeout inside the
  eight-read `RefreshAllAsync` chain leaves stale or empty data with no visible feedback.
- **Unnamed touch ids.** Gesture `0x01` (single press, plays/pauses), `0x05` (action `0x07`,
  possibly the swipe), `0x06`, and the whole context `0x06` (likely calls, actions `0x1C` /
  `0x1D`). Shown as read, not offered. Naming them needs the phone app side by side.
- `JsonDeviceProfile` — still deferred from Stage 2.

## Rules, unchanged

- Never fuzz the `0x04xx` space. Only allowlisted ids are transmitted.
- Full read-only snapshot before the first SET of a session; restored in `finally`, including
  on Ctrl+C.
- Touch config is never rebuilt from a model.
- Firmware OTA is permanently out of scope.
- No brute-force ANC sweeps.
- Gadgetbridge is AGPL-3.0: research reference only, no Java copied.
