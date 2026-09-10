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


def tier_b_anc(session: Session, log: ProtocolLog) -> dict:
    """Derive the Buds 4 ANC readback mask by setting each mode and re-reading.

    This is the measurement that resolves the documented disagreement between
    the Buds Pro 3 and Gadgetbridge encodings.
    """
    print("\n  Tier B -- ANC mode map (audible, restored afterwards)")
    print("  " + "-" * 62)
    original = session.request(known.ANC_GET, bytes((0x01, 0x01)))
    if original is None:
        print("  cannot read current ANC state -- aborting tier B rather than")
        print("  changing a setting we would not be able to put back.")
        return {"error": "no baseline"}
    print(f"  baseline: {original.payload.hex(' ')}")

    observed: dict[str, dict] = {}
    try:
        for name, value in known.ANC_MODE_SET.items():
            ack = session.send_set(known.ANC_SET, bytes((0x01, 0x01, value)))
            time.sleep(1.0)
            readback = session.request(known.ANC_GET, bytes((0x01, 0x01)))
            entry = {
                "set_byte": f"0x{value:02X}",
                "ack": "timeout" if ack is None else ("ok" if ack.ok else f"status 0x{ack.status:02X}"),
                "readback": readback.payload.hex() if readback else "timeout",
                "interpreted": _anc_mode_from_reply(readback.payload) if readback else None,
            }
            observed[name] = entry
            print(f"  set {name:<13} -> {entry['readback']}  ({entry['interpreted'] or 'unrecognised mask'})")
            log.probe("set", known.ANC_SET, entry["ack"], mode=name, readback=entry["readback"])
            time.sleep(1.2)
    finally:
        session.send_set(known.ANC_SET, bytes((0x01, 0x01, known.ANC_MODE_SET["off"])))
        print("  restored ANC to off")
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


TIERS = {"A": tier_a_find_device, "B": tier_b_anc, "C": tier_c_eq}


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