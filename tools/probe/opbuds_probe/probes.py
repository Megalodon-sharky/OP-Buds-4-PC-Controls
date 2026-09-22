"""Probe routines: read-only sweeps, and tiered SET probing with restore.

Safety model, enforced here and in Session.send_set:

* The 0x04xx (SET) space is never fuzzed. Only commands on
  known.SET_ALLOWLIST are ever transmitted, because an unknown setter could be
  a factory reset or a DFU entry point.
* A full read-only snapshot is taken before the first SET of any run, and
  restored in a finally block -- including on Ctrl+C.
* Tiers escalate by blast radius: A changes nothing persistent, B and C change
  one audible setting that we read back and restore, D touches multi-field
  config and only runs once A-C have proven the encoding.
"""

from __future__ import annotations

import json
import time
from pathlib import Path

from . import known
from .frames import Frame
from .proto_log import ProtocolLog
from .session import ConnectionLost, Session


# --- read-only ------------------------------------------------------------

def get_sweep(session: Session, log: ProtocolLog, gap: float = 1.2, timeout: float = 3.0) -> dict:
    """Send every known read request. Timeouts are recorded as results."""
    results: dict[str, dict] = {}
    print("\n  Read-only sweep of known commands")
    print("  " + "-" * 62)
    for command, payload in known.GET_SWEEP:
        name = known.command_name(command)
        suffix = f" payload={payload.hex(' ')}" if payload else ""
        try:
            reply = session.request(command, payload, timeout=timeout)
        except ConnectionLost as exc:
            print(f"\n  {exc}")
            print(f"  Stopping with {len(results)} result(s) kept.")
            log.event("sweep aborted: link lost", level="warn")
            break
        if reply is None:
            print(f"  0x{command:04X}  {name:<22} timeout{suffix}")
            log.probe("get", command, "timeout", payload=payload.hex())
            results[f"0x{command:04X}"] = {"result": "timeout", "payload": payload.hex()}
        else:
            print(f"  0x{command:04X}  {name:<22} {reply.payload.hex(' ') or '(empty)'}")
            log.probe("get", command, "reply", payload=payload.hex(), reply=reply.payload.hex())
            results[f"0x{command:04X}"] = {
                "result": "reply",
                "payload": payload.hex(),
                "reply": reply.payload.hex(),
                "seq": reply.seq,
            }
        time.sleep(gap)
    return results


def unknown_get_scan(
    session: Session, log: ProtocolLog, lo: int = 0x00, hi: int = 0x30,
    gap: float = 0.8, timeout: float = 2.0,
) -> dict:
    """Sweep request ids lo..hi at message type 0x01. Reads only -- always safe."""
    found: dict[str, str] = {}
    known_ids = {c & 0xFF for c, _ in known.GET_SWEEP}
    print(f"\n  Scanning unknown request ids 0x{lo:02X}..0x{hi:02X} (read-only)")
    print("  " + "-" * 62)
    for cmd_id in range(lo, hi + 1):
        if cmd_id in known_ids:
            continue
        command = 0x0100 | cmd_id
        try:
            reply = session.request(command, b"", timeout=timeout)
        except ConnectionLost as exc:
            print(f"\n  {exc}")
            print(f"  Scanned up to 0x{cmd_id:02X}; {len(found)} new id(s) found before the drop.")
            log.event("scan aborted: link lost", level="warn", last_id=f"0x{cmd_id:02X}")
            break
        if reply is not None:
            print(f"  0x{command:04X}  NEW  payload={reply.payload.hex(' ') or '(empty)'}")
            log.probe("get-unknown", command, "reply", reply=reply.payload.hex())
            found[f"0x{command:04X}"] = reply.payload.hex()
        time.sleep(gap)
    if not found:
        print("  (no undocumented request ids answered)")
    return found


# --- snapshot / restore ---------------------------------------------------

def snapshot(session: Session, path: Path, timeout: float = 3.0) -> dict:
    """Capture every readable setting verbatim so it can be written back.

    Values are stored as raw hex and replayed as-is. Nothing is parsed and
    reconstructed -- a restore built from a half-understood model is how you
    turn a probe run into a bricked config.
    """
    state: dict[str, str] = {}
    for command, payload in known.GET_SWEEP:
        reply = session.request(command, payload, timeout=timeout)
        if reply is not None:
            state[f"0x{command:04X}"] = reply.payload.hex()
        time.sleep(0.6)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(state, indent=2), encoding="utf-8")
    print(f"  snapshot written to {path}")
    return state


def _anc_mode_from_reply(payload: bytes) -> str | None:
    """Interpret an ANC reply payload as a mode name, if the mask is recognised.

    Layout per the Buds Pro 3: [status][?][?][field][?][maskLo][maskHi].
    Buds 4 offsets are unverified, so the mask is searched for rather than read
    from a fixed offset.
    """
    for off in range(len(payload) - 1):
        mask = payload[off] | (payload[off + 1] << 8)
        if mask in known.ANC_READBACK_MASK:
            return known.ANC_READBACK_MASK[mask]
    return None


# --- tiers ----------------------------------------------------------------

def tier_a_find_device(session: Session, log: ProtocolLog) -> dict:
    """Ring the earbuds. No persistent state, unmistakable audible feedback."""
    print("\n  Tier A -- find device (audible, nothing to restore)")
    print("  " + "-" * 62)
    results = {}
    for label, payload in (("empty", b""), ("01", b"\x01"), ("0101", b"\x01\x01"), ("0001", b"\x00\x01")):
        ack = session.send_set(known.FIND_DEVICE_SET, payload)
        if ack is None:
            print(f"  payload {label:<6} timeout")
            results[label] = "timeout"
            continue
        state = "OK" if ack.ok else f"rejected (status 0x{ack.status:02X})"
        print(f"  payload {label:<6} {state}")
        log.probe("set", known.FIND_DEVICE_SET, state, payload=payload.hex())
        results[label] = state
        if ack.ok:
            print("  -> earbuds should be beeping. Stopping here.")
            break
        time.sleep(1.5)
    return results


#: ANC set bytes to sweep in Tier B. 0x01..0x08 mapped off/NC/transparency and
#: showed the field is a bitmask where "off" wins any conflict. Adaptive reads
#: back as 0x0800 and is NOT reachable in that range, so the sweep extends to
#: 0x20 to find it. Values are parameters to an allowlisted setter, not command
#: ids, and the device clamps unrecognised ones to "off" rather than misbehaving.
ANC_SET_SWEEP = range(0x01, 0x21)


def _anc_mask(payload: bytes) -> int | None:
    """Mask from a confirmed ANC reply ``[status][count][id][maskLo][maskHi]``."""
    if len(payload) < 5 or payload[0] != 0x00:
        return None
    return payload[3] | (payload[4] << 8)


def tier_b_anc(session: Session, log: ProtocolLog) -> dict:
    """Map ANC *set* bytes onto the confirmed readback masks.

    The readback masks are already measured (off 0x0008, NC 0x0010,
    transparency 0x0100, adaptive 0x0800). What is still unknown is which byte
    to WRITE to reach each of them -- set and readback occupy different numeric
    spaces, so one cannot be derived from the other.

    Two deliberate choices:

    * The sweep runs 0x01..0x08 rather than the three Buds Pro 3 values, because
      those three cannot reach adaptive, which this hardware has.
    * Restore replays whichever set byte reproduced the ORIGINAL mask, learned
      during the sweep. Restoring to a hardcoded "off" would silently leave the
      earbuds in a mode the user never chose.

    An ack of 0x00 does NOT prove the mode changed -- Tier A returned OK for
    three writes that did nothing at all. Every step is verified by reading the
    mode back.
    """
    print("\n  Tier B -- ANC set-byte map (audible, restored afterwards)")
    print("  " + "-" * 62)
    original = session.request(known.ANC_GET, bytes((0x01, 0x01)))
    if original is None:
        print("  cannot read current ANC state -- aborting tier B rather than")
        print("  changing a setting we would not be able to put back.")
        return {"error": "no baseline"}
    baseline_mask = _anc_mask(original.payload)
    baseline_name = known.ANC_READBACK_MASK.get(baseline_mask, "unrecognised")
    print(f"  baseline: {original.payload.hex(' ')}  "
          f"mask=0x{baseline_mask:04X} ({baseline_name})")

    observed: dict[str, dict] = {}
    restore_byte: int | None = None
    try:
        for value in ANC_SET_SWEEP:
            ack = session.send_set(known.ANC_SET, bytes((0x01, 0x01, value)))
            time.sleep(1.0)
            readback = session.request(known.ANC_GET, bytes((0x01, 0x01)))
            mask = _anc_mask(readback.payload) if readback else None
            name = known.ANC_READBACK_MASK.get(mask) if mask is not None else None
            entry = {
                "set_byte": f"0x{value:02X}",
                "ack": "timeout" if ack is None else ("ok" if ack.ok else f"status 0x{ack.status:02X}"),
                "mask": f"0x{mask:04X}" if mask is not None else None,
                "mode": name,
            }
            observed[f"0x{value:02X}"] = entry
            verdict = "no change / rejected" if entry["ack"] != "ok" else (
                name or (f"unknown mask {entry['mask']}" if mask is not None else "unreadable"))
            print(f"  set 0x{value:02X}  ack={entry['ack']:<12} mask={entry['mask'] or '--':<8} {verdict}")
            log.probe("set", known.ANC_SET, entry["ack"], set_byte=value,
                      mask=entry["mask"], mode=name)
            if mask is not None and mask == baseline_mask and restore_byte is None:
                restore_byte = value
            time.sleep(1.2)
    finally:
        if restore_byte is not None:
            session.send_set(known.ANC_SET, bytes((0x01, 0x01, restore_byte)))
            time.sleep(0.8)
            check = session.request(known.ANC_GET, bytes((0x01, 0x01)))
            got = _anc_mask(check.payload) if check else None
            ok = got == baseline_mask
            print(f"  restored ANC to 0x{restore_byte:02X} -> mask "
                  f"0x{got:04X} ({'matches baseline' if ok else 'DOES NOT MATCH'})"
                  if got is not None else "  restore readback failed")
        else:
            print(f"  *** COULD NOT RESTORE: no set byte reproduced the original mask "
                  f"0x{baseline_mask:04X} ({baseline_name}). ***")
            print("  Set the ANC mode from your phone to put it back.")
    return observed


def tier_c_eq(session: Session, log: ProtocolLog) -> dict:
    """Sweep EQ preset ids 0x00-0x07 to derive the Buds 4's 3-preset map.

    The Buds 4 has three presets where the Buds Pro 3 has five, so the ids are
    expected to differ. Ids that are rejected tell us as much as ids that work.
    """
    print("\n  Tier C -- EQ preset ids (audible, restored afterwards)")
    print("  " + "-" * 62)
    original = session.request(known.EQ_PRESET_GET)
    if original is None:
        print("  cannot read current EQ preset -- aborting tier C.")
        return {"error": "no baseline"}
    print(f"  baseline: {original.payload.hex(' ')}")

    observed: dict[str, dict] = {}
    try:
        for value in range(0x00, 0x08):
            ack = session.send_set(known.EQ_PRESET_SET, bytes((value,)))
            time.sleep(0.8)
            readback = session.request(known.EQ_PRESET_GET)
            accepted = ack is not None and ack.ok
            entry = {
                "accepted": accepted,
                "ack": "timeout" if ack is None else ("ok" if ack.ok else f"status 0x{ack.status:02X}"),
                "readback": readback.payload.hex() if readback else "timeout",
            }
            observed[f"0x{value:02X}"] = entry
            mark = "accepted" if accepted else "rejected"
            print(f"  preset 0x{value:02X}  {mark:<9} readback={entry['readback']}")
            log.probe("set", known.EQ_PRESET_SET, entry["ack"], preset=value, readback=entry["readback"])
            time.sleep(1.0)
    finally:
        # Reply payload is [status][preset]; replay that preset byte as-is
        # rather than rebuilding it from a parsed model.
        if len(original.payload) >= 2:
            session.send_set(known.EQ_PRESET_SET, bytes((original.payload[1],)))
            print(f"  restored EQ preset to 0x{original.payload[1]:02X}")
    return observed


# --- Tier D: BassWave level and touch config --------------------------------
# First transmissions of both setters. Neither payload is a guess: independent
# lines of evidence agree on each before a byte is sent.
#
# * On this hardware every confirmed setter takes its getter's reply minus the
#   status byte: 0x0406 sends [preset] for 0x010F's [status][preset], and 0x0404
#   sends [count][id][lo][hi] for 0x010C's [status][count][id][lo][hi].
# * BassWave: the Buds Pro 3 sends 0x041B as [min][max][level] -- 0x0124's reply
#   minus status, and byte for byte the body of the 0x0508 push.
# * Touch: Gadgetbridge's Oppo encoding is [count=1][side], a 16-bit gesture code,
#   then an action. Its codes (0x0201 = double tap) are little-endian
#   [type 01][gesture 02], so the payload is one 0x0108 record behind a count --
#   the same shape as ANC.
#
# Each tier still tries at most two shapes, verifies by readback rather than by
# ack (an ack proves parsing, not effect), diffs every readable command to catch
# collateral changes, and restores from the bytes it read, never a rebuilt model.

def _signed(b: int) -> int:
    return b - 0x100 if b & 0x80 else b


def _ack_text(ack: Frame | None) -> str:
    return "timeout" if ack is None else ("ok" if ack.ok else f"status 0x{ack.status:02X}")


def _collateral(before: dict, after: dict, expected: str) -> list[dict]:
    """Show everything that changed; single out anything other than ``expected``."""
    changes = diff_snapshots(before, after)
    render_diff(changes)
    stray = [c for c in changes if c["command"] != expected]
    if stray:
        print(f"\n  {len(stray)} command(s) besides {expected} changed -- review above.")
        print("  Battery drifting by a percent is normal; anything else is not.")
    else:
        print(f"\n  Collateral check: only {expected} changed.")
    return stray


def tier_d_basswave(session: Session, log: ProtocolLog) -> dict:
    """First transmission of 0x041B, the BassWave level setter.

    Moves the level ONE step and puts it back. An adjacent value is the smallest
    audible change available, and one the device already reports as inside its
    own range. Min and max are replayed verbatim from the reply, never assumed.
    """
    print("\n  Tier BASS -- BassWave level (one audible step, restored)")
    print("  " + "-" * 62)
    original = session.request(known.BASSWAVE_VALUE_GET)
    if original is None or len(original.payload) < 4 or original.payload[0] != 0x00:
        print("  cannot read BassWave -- aborting rather than writing blind.")
        return {"error": "no baseline"}
    baseline = original.payload
    _, lo, hi, cur = baseline[:4]
    lo_s, hi_s, cur_s = _signed(lo), _signed(hi), _signed(cur)
    target_s = cur_s - 1 if cur_s > lo_s else cur_s + 1
    target = target_s & 0xFF
    print(f"  baseline: {baseline.hex(' ')}  (min {lo_s:+d}, max {hi_s:+d}, level {cur_s:+d})")
    feature = session.request(known.MISC_CONFIG_GET, bytes((0x01, known.BASSWAVE_FEATURE_ID)))
    print(f"  feature 0x1D (BassWave switch): {feature.payload.hex(' ') if feature else 'timeout'}")
    print(f"  target: level {target_s:+d}")

    shapes = {
        "min-max-level": lambda level: bytes((lo, hi, level)),
        "level-only": lambda level: bytes((level,)),
    }
    print("\n  Reading every command id first, for the collateral diff...")
    before = read_all(session)
    worked: str | None = None
    observed: dict = {"baseline": baseline.hex()}
    try:
        for label, build in shapes.items():
            session.notifications.clear()
            payload = build(target)
            ack = session.send_set(known.BASSWAVE_LEVEL_SET, payload)
            time.sleep(1.0)
            readback = session.request(known.BASSWAVE_VALUE_GET)
            got = readback.payload if readback else None
            pushes = [f.payload.hex(" ") for f in session.notifications
                      if f.is_notification and f.cmd_id == 0x08]
            entry = {"payload": payload.hex(), "ack": _ack_text(ack),
                     "readback": got.hex() if got else "timeout", "push_0508": pushes}
            observed[label] = entry
            print(f"  {label:<14} tx {payload.hex(' '):<9} ack={entry['ack']:<12} "
                  f"readback={entry['readback']}  push={pushes or '-'}")
            log.probe("set", known.BASSWAVE_LEVEL_SET, entry["ack"], shape=label,
                      payload=entry["payload"], readback=entry["readback"], pushes=pushes)
            if got is not None and len(got) >= 4 and got[3] == target:
                worked = label
                break
            if got is not None and got != baseline:
                print("  *** the level moved, but not to the target -- stopping to restore. ***")
                break
            time.sleep(1.0)

        if worked:
            print(f"\n  CONFIRMED: '{worked}' moved the level {cur_s:+d} -> {target_s:+d}.")
            print("  Re-reading every command id for collateral changes...")
            after = read_all(session)
            observed["stray"] = _collateral(before, after, f"0x{known.BASSWAVE_VALUE_GET:04X}")
        else:
            print("\n  Neither shape moved the level. Stopping here, as planned --")
            print("  no further payloads are tried.")
    finally:
        observed["restored"] = _restore_basswave(session, baseline, shapes, worked)
    return observed


def _restore_basswave(session: Session, baseline: bytes, shapes: dict, worked: str | None) -> bool:
    now = session.request(known.BASSWAVE_VALUE_GET)
    if now is not None and now.payload == baseline:
        print(f"  BassWave is at its baseline ({baseline.hex(' ')}) -- nothing to restore.")
        return True
    order = ([worked] if worked else []) + [s for s in shapes if s != worked]
    for label in order:
        session.send_set(known.BASSWAVE_LEVEL_SET, shapes[label](baseline[3]))
        time.sleep(1.0)
        now = session.request(known.BASSWAVE_VALUE_GET)
        if now is not None and now.payload == baseline:
            print(f"  restored with '{label}': {now.payload.hex(' ')} matches the baseline.")
            return True
    print(f"  *** COULD NOT RESTORE BassWave to {baseline.hex(' ')}. "
          f"Set level {_signed(baseline[3]):+d} from the phone. ***")
    return False


def tier_d_touch(session: Session, log: ProtocolLog) -> dict:
    """First transmission of 0x0401, the touch-config setter.

    Changes exactly one record: the RIGHT bud's double tap takes the LEFT bud's
    double-tap action -- a value this exact type and gesture already holds on this
    unit, so nothing new is introduced. Then it is put back and the whole table is
    compared byte for byte with what was read.
    """
    print("\n  Tier TOUCH -- touch config (silent; one record changed, restored)")
    print("  " + "-" * 62)
    original = session.request(known.TOUCH_CONFIG_GET)
    raw = original.payload if original else b""
    if len(raw) < 2 or raw[0] != 0x00 or len(raw) != 2 + 4 * raw[1]:
        print(f"  unexpected 0x0108 reply {raw.hex(' ') or '(none)'} -- aborting.")
        return {"error": "no baseline"}
    count = raw[1]
    records = [raw[2 + 4 * i: 6 + 4 * i] for i in range(count)]

    def find(side: int, type_: int, gesture: int) -> int | None:
        return next((i for i, r in enumerate(records)
                     if r[0] == side and r[1] == type_ and r[2] == gesture), None)

    ri, li = find(0x02, 0x01, 0x02), find(0x01, 0x01, 0x02)
    if ri is None or li is None or records[ri][3] == records[li][3]:
        print("  no right/left double-tap pair with differing actions -- the probe")
        print("  target does not exist on this unit. Aborting rather than improvising.")
        return {"error": "no target", "table": raw.hex()}

    new_action = records[li][3]
    record = bytes((0x02, 0x01, 0x02, new_action))
    offset = 2 + 4 * ri + 3
    expected = raw[:offset] + bytes((new_action,)) + raw[offset + 1:]
    print(f"  table: {count} records; right double tap 0x{records[ri][3]:02X} -> 0x{new_action:02X}")

    shapes = {
        "count+record": b"\x01" + record,
        "full-table": bytes((count,)) + expected[2:],
    }
    print("\n  Reading every command id first, for the collateral diff...")
    before = read_all(session)
    worked: str | None = None
    observed: dict = {"baseline": raw.hex()}
    try:
        for label, payload in shapes.items():
            ack = session.send_set(known.TOUCH_CONFIG_SET, payload)
            time.sleep(1.0)
            readback = session.request(known.TOUCH_CONFIG_GET)
            got = readback.payload if readback else None
            spots = ([i for i, (x, y) in enumerate(zip(raw, got)) if x != y]
                     if got is not None and len(got) == len(raw) else None)
            entry = {"payload": payload.hex(), "ack": _ack_text(ack),
                     "readback": got.hex() if got else "timeout", "changed_bytes": spots}
            observed[label] = entry
            shown = payload.hex(" ") if len(payload) <= 8 else f"{payload[:8].hex(' ')} ...({len(payload)}B)"
            print(f"  {label:<13} tx {shown}  ack={entry['ack']}  changed bytes={spots}")
            log.probe("set", known.TOUCH_CONFIG_SET, entry["ack"], shape=label,
                      payload=entry["payload"], readback=entry["readback"])
            if got == expected:
                worked = label
                break
            if got is not None and got != raw:
                print("  *** the table changed, but not as intended -- stopping to restore. ***")
                break
            time.sleep(1.0)

        if worked:
            print(f"\n  CONFIRMED: '{worked}' changed exactly byte {offset} and nothing else.")
            print("  Re-reading every command id for collateral changes...")
            after = read_all(session)
            observed["stray"] = _collateral(before, after, f"0x{known.TOUCH_CONFIG_GET:04X}")
        else:
            print("\n  Neither shape changed the table. Stopping here, as planned.")
    finally:
        observed["restored"] = _restore_touch(session, raw, records, worked)
    return observed


def _restore_touch(session: Session, raw: bytes, records: list[bytes], worked: str | None) -> bool:
    """Write back the table exactly as read. Records are replayed, never rebuilt."""
    now = session.request(known.TOUCH_CONFIG_GET)
    if now is not None and now.payload == raw:
        print("  touch table matches the baseline byte for byte -- nothing to restore.")
        return True
    attempts: list[tuple[str, list[bytes]]] = []
    if worked != "full-table":
        cur = now.payload if now is not None and len(now.payload) == len(raw) else None
        stale = [i for i, rec in enumerate(records) if cur is None or cur[2 + 4 * i: 6 + 4 * i] != rec]
        attempts.append(("count+record", [b"\x01" + records[i] for i in stale]))
    attempts.append(("full-table", [bytes((len(records),)) + raw[2:]]))
    for label, payloads in attempts:
        for payload in payloads:
            session.send_set(known.TOUCH_CONFIG_SET, payload)
            time.sleep(0.6)
        now = session.request(known.TOUCH_CONFIG_GET)
        if now is not None and now.payload == raw:
            print(f"  restored with '{label}': table matches the baseline byte for byte.")
            return True
    print("  *** COULD NOT RESTORE the touch table. Original bytes, for the record: ***")
    print(f"  {raw.hex(' ')}")
    print("  Reassign the gestures from the phone, or reset the earbuds.")
    return False


TIERS = {"A": tier_a_find_device, "B": tier_b_anc, "C": tier_c_eq,
         "BASS": tier_d_basswave, "TOUCH": tier_d_touch}


# --- differential scanning ------------------------------------------------
# Static reasoning can only get so far with an undocumented command. Reading the
# whole command space, changing exactly one setting, reading it again and diffing
# identifies commands definitively and in seconds. This is the highest-value
# technique available and it is entirely read-only.

#: Request ids that answered a read during the initial sweep of 0x00..0x60.
#: Anything above 0x31 was silent.
RESPONDING_IDS = list(range(0x00, 0x32))

#: Commands that need a payload to answer usefully. Sending an empty payload to
#: these produces a complaint rather than data.
PAYLOAD_FOR_ID = {
    0x0C: bytes((0x01, 0x01)),
    0x0D: bytes((0x01, 0x1D)),
}


def read_all(session: Session, ids=None, gap: float = 0.35, timeout: float = 2.0) -> dict:
    """Read every responding command id once. Returns {"0x01NN": "hex" | None}."""
    out: dict[str, str | None] = {}
    for cmd_id in (RESPONDING_IDS if ids is None else ids):
        command = 0x0100 | cmd_id
        payload = PAYLOAD_FOR_ID.get(cmd_id, b"")
        try:
            reply = session.request(command, payload, timeout=timeout)
        except ConnectionLost as exc:
            print(f"\n  {exc}")
            print(f"  Snapshot is INCOMPLETE (stopped at 0x{cmd_id:02X}) -- do not diff against it.")
            raise
        out[f"0x{command:04X}"] = reply.payload.hex() if reply is not None else None
        time.sleep(gap)
    return out


def snapshot_all(session: Session, path: Path, label: str = "") -> dict:
    """Read the whole command space and save it for later diffing."""
    print(f"\n  Reading {len(RESPONDING_IDS)} command ids...")
    state = read_all(session)
    answered = sum(1 for v in state.values() if v is not None)
    doc = {"label": label, "answered": answered, "state": state}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    print(f"  {answered}/{len(state)} answered; saved to {path}")
    return doc


# --- live differential watch ----------------------------------------------
# Every settings-bearing getter here is count-prefixed: the request carries a
# count then a list of feature ids, and the reply carries only the ids the
# hardware actually supports. That makes the whole misc-config surface one round
# trip, so the entire settings state can be re-read every couple of seconds and
# diffed live. Naming an unknown feature then costs one toggle on the phone
# rather than a snapshot pair.

#: Misc-config feature ids this hardware answered for. Measured, not assumed:
#: ids 0x01..0x40 were requested and exactly these came back.
MISC_IDS_BUDS4 = (0x02, 0x04, 0x05, 0x06, 0x0B, 0x0C, 0x0D, 0x11, 0x12,
                  0x14, 0x16, 0x17, 0x18, 0x19, 0x1B, 0x1D, 0x1E)


def misc_request_payload(ids=MISC_IDS_BUDS4) -> bytes:
    return bytes((len(ids),)) + bytes(ids)


#: Named reads, where a curated payload beats the generic empty one.
WATCH_SPECIAL = {
    0x0D: (misc_request_payload(), "misc"),
    0x0C: (bytes((0x01, 0x01)), "anc"),
    0x0F: (b"", "eq-preset"),
    0x24: (b"", "basswave"),
    0x22: (b"", "custom-eq"),
    0x08: (b"", "touch"),
    0x06: (b"", "battery"),
    0x09: (b"", "wear"),
    0x05: (b"", "firmware"),
    0x2F: (bytes((0x01, 0x01)), "cmd-2F"),
}


def _build_watch_reads() -> list[tuple[int, bytes, str]]:
    """Poll the WHOLE readable space, not a curated subset.

    A narrow read list is how a setting change gets missed: three settings were
    toggled on the phone during one run and none of them touched any of the
    fourteen commands then being polled. Everything that answers is cheap to
    read, so read everything.
    """
    reads = []
    for cmd_id in RESPONDING_IDS:
        payload, label = WATCH_SPECIAL.get(
            cmd_id, (PAYLOAD_FOR_ID.get(cmd_id, b""), f"cmd-{cmd_id:02X}")
        )
        reads.append((0x0100 | cmd_id, payload, label))
    return reads


#: (command, payload, label). One poll cycle reads all of these.
WATCH_READS = _build_watch_reads()


def _decode_kv(payload: bytes, width: int) -> dict[int, str] | None:
    """[status][count] then count records of ``width`` bytes, id first."""
    if len(payload) < 2 or payload[0] != 0x00:
        return None
    count = payload[1]
    out: dict[int, str] = {}
    for i in range(count):
        off = 2 + i * width
        if off + width > len(payload):
            return None
        out[payload[off]] = payload[off + 1 : off + width].hex()
    return out


def watch_loop(session: Session, log: ProtocolLog, duration: float,
               interval: float = 0.8, gap: float = 0.06, timeout: float = 2.0) -> None:
    """Poll the whole settings surface and print only what changes."""
    prev: dict[str, str | None] = {}
    prev_misc: dict[int, str] = {}
    start = time.monotonic()
    cycle = 0

    print(f"\n  Watching for {duration:.0f}s. Change ONE setting on the phone, wait for")
    print("  it to be reported here, then change the next one.")
    print("  " + "-" * 68)

    while time.monotonic() - start < duration:
        cycle += 1
        for command, payload, label in WATCH_READS:
            try:
                reply = session.request(command, payload, timeout=timeout)
            except ConnectionLost as exc:
                print(f"\n  {exc}")
                return
            now = reply.payload.hex() if reply is not None else None
            was = prev.get(label, "__unset__")
            if was == "__unset__":
                prev[label] = now
                if label == "misc" and reply is not None:
                    prev_misc = _decode_kv(reply.payload, 2) or {}
                continue
            if now != was:
                stamp = time.monotonic() - start
                if label == "misc" and reply is not None:
                    cur = _decode_kv(reply.payload, 2) or {}
                    for fid in sorted(set(prev_misc) | set(cur)):
                        old, new = prev_misc.get(fid), cur.get(fid)
                        if old != new:
                            print(f"  [{stamp:7.1f}s]  misc feature 0x{fid:02X}: "
                                  f"{old or '(absent)'} -> {new or '(absent)'}")
                            log.event("watch change", label="misc",
                                      feature=f"0x{fid:02X}", before=old, after=new)
                    prev_misc = cur
                else:
                    print(f"  [{stamp:7.1f}s]  {label:<10} {was or '(no reply)'} "
                          f"-> {now or '(no reply)'}")
                    log.event("watch change", label=label, before=was, after=now)
                prev[label] = now
            time.sleep(gap)
        time.sleep(interval)
    print(f"\n  {cycle} poll cycle(s) completed.")


def diff_snapshots(before: dict, after: dict) -> list[dict]:
    """Commands whose reply changed between two snapshots."""
    a, b = before.get("state", before), after.get("state", after)
    changes = []
    for key in sorted(set(a) | set(b)):
        old, new = a.get(key), b.get(key)
        if old != new:
            changes.append({"command": key, "before": old, "after": new})
    return changes


def render_diff(changes: list[dict]) -> None:
    if not changes:
        print("\n  No command changed. Either the setting is not exposed over this")
        print("  protocol, or it lives behind a command that needs a payload we did")
        print("  not send, or the change had not propagated yet.")
        return
    print(f"\n  {len(changes)} command(s) changed:")
    print("  " + "-" * 68)
    for ch in changes:
        print(f"\n  {ch['command']}  {known.command_name(int(ch['command'], 16))}")
        print(f"    before: {ch['before'] if ch['before'] is not None else '(no reply)'}")
        print(f"    after : {ch['after'] if ch['after'] is not None else '(no reply)'}")
        old, new = ch["before"], ch["after"]
        if old and new and len(old) == len(new):
            ob, nb = bytes.fromhex(old), bytes.fromhex(new)
            spots = [i for i, (x, y) in enumerate(zip(ob, nb)) if x != y]
            for i in spots:
                print(f"    byte {i}: 0x{ob[i]:02X} -> 0x{nb[i]:02X}")