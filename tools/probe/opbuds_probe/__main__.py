"""Command line entry point:  python -m opbuds_probe <subcommand>"""

from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from pathlib import Path

from . import known, probes
from .btsock import connect_channel, connect_uuid, describe_error
from .devices import find_buds, list_paired, query_rfcomm_services
from .frames import FrameParser, annotate
from .proto_log import ProtocolLog, default_name, read_records
from .session import Session

CAPTURES = Path(__file__).resolve().parents[3] / "captures"


def _stamp() -> str:
    return time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())


def _resolve_mac(args) -> str:
    if getattr(args, "mac", None):
        return args.mac
    dev = find_buds()
    if dev is None:
        sys.exit("No paired device with 'buds' in its name. Pass --mac explicitly, "
                 "or run 'devices' to see what is paired.")
    print(f"  using {dev.name} ({dev.mac})")
    return dev.mac


def _open_log(args, subcommand: str, mac: str, extra: dict | None = None) -> ProtocolLog:
    CAPTURES.mkdir(parents=True, exist_ok=True)
    path = CAPTURES / default_name(mac, subcommand, _stamp())
    meta = {"mac": mac, "subcommand": subcommand, "transport": "rfcomm",
            "python": sys.version.split()[0]}
    if getattr(args, "note", None):
        meta["operator_notes"] = args.note
    meta.update(extra or {})
    print(f"  logging to {path}")
    return ProtocolLog(path, meta)


def _connect(args, mac: str) -> tuple[socket.socket, int | None]:
    """Channel if given, otherwise let Windows resolve the service UUID via SDP."""
    if getattr(args, "channel", None):
        print(f"  connecting to channel {args.channel}...")
        return connect_channel(mac, args.channel, timeout=args.timeout), args.channel
    print(f"  connecting by service UUID {known.OPO_SERVICE_UUID} (Windows resolves the channel)...")
    sock, channel = connect_uuid(mac, known.OPO_SERVICE_UUID, timeout=args.timeout)
    print(f"  connected{f' on channel {channel}' if channel else ''}")
    return sock, channel


# --- subcommands ----------------------------------------------------------

def cmd_devices(args) -> int:
    devices = list_paired()
    if not devices:
        print("No paired Bluetooth devices found.")
        return 1
    print(f"\n  {len(devices)} paired device(s); * marks ones this tool may support\n")
    for dev in devices:
        print(f"  {dev}")
    return 0


_UUID_TAGS = {
    known.OPO_SERVICE_UUID: "<-- OPO control service, this is the one we want",
    known.FASTPAIR_MESSAGE_STREAM_UUID: "(Google Fast Pair Message Stream)",
    known.UNKNOWN_VENDOR_UUID: "(unknown vendor SPP -- worth a probe)",
    "0000111e-0000-1000-8000-00805f9b34fb": "(Handsfree profile)",
    "0000110a-0000-1000-8000-00805f9b34fb": "(Audio source)",
    "0000110b-0000-1000-8000-00805f9b34fb": "(Audio sink)",
}


def cmd_sdp(args) -> int:
    mac = _resolve_mac(args)
    print(f"\n  Querying RFCOMM services on {mac}")

    # An uncached query gives live data including the real channel number, but
    # only works while the device is connected. The cached record survives
    # disconnection but usually reports channel 0. Try both and report which.
    attempts = [(True, "uncached"), (False, "cached")] if args.uncached else [(False, "cached")]
    result = None
    for uncached, label in attempts:
        try:
            candidate = query_rfcomm_services(mac, uncached=uncached)
        except RuntimeError as exc:
            print(f"  {label}: {exc}")
            continue
        if candidate.services:
            print(f"  source: {label}   device is "
                  f"{'connected' if candidate.connected else 'disconnected'}")
            result = candidate
            break
        print(f"  {label}: no services returned")

    if result is None:
        print("\n  Nothing came back. Connect the earbuds (play audio through them)")
        print("  and try again -- an uncached query needs a live link.")
        return 1

    print()
    opo_channel = None
    for svc in result.services:
        tag = _UUID_TAGS.get(svc.uuid, "")
        channel = f"channel {svc.channel}" if svc.channel else "channel unknown"
        print(f"  {svc.uuid}  {channel:<16} {tag}")
        if svc.uuid == known.OPO_SERVICE_UUID:
            opo_channel = svc.channel

    opo = result.find(known.OPO_SERVICE_UUID)
    print()
    if opo is None:
        print("  The OPO control service is NOT advertised. This device probably")
        print("  cannot be controlled by this tool.")
        return 1

    print("  The OPO control service is present.")
    if opo_channel:
        print(f"  Resolved channel: {opo_channel}")
        print(f"  Next: python -m opbuds_probe handshake --mac {mac} --channel {opo_channel}")
    else:
        print("  Channel not reported (normal for a cached record on a disconnected")
        print("  device). You do not need it -- omit --channel and Windows will")
        print("  resolve the service by UUID:")
        print(f"    python -m opbuds_probe handshake --mac {mac}")
    return 0

def cmd_scan(args) -> int:
    mac = _resolve_mac(args)
    print(f"\n  Scanning RFCOMM channels {args.start}..{args.end} on {mac}")
    print(f"  Skipping {sorted(known.AUDIO_PROFILE_CHANNELS)} so audio is not disturbed.")
    print("  " + "-" * 62)
    open_channels = []
    for channel in range(args.start, args.end + 1):
        if channel in known.AUDIO_PROFILE_CHANNELS:
            print(f"  channel {channel:>2}  skipped (audio profile)")
            continue
        try:
            sock = connect_channel(mac, channel, timeout=args.timeout)
        except Exception as exc:
            print(f"  channel {channel:>2}  {describe_error(exc)}")
        else:
            print(f"  channel {channel:>2}  OPEN")
            open_channels.append(channel)
            sock.close()
        time.sleep(args.gap)

    print("  " + "-" * 62)
    if open_channels:
        print(f"  open channels: {open_channels}")
        print(f"  try: python -m opbuds_probe handshake --mac {mac} --channel {open_channels[0]}")
    else:
        print("  No channel accepted a connection. The earbuds may be in the case,")
        print("  or the phone may be holding the control channel.")
    return 0 if open_channels else 1


def cmd_handshake(args) -> int:
    mac = _resolve_mac(args)
    token = known.REGISTER_TOKENS.get(args.token)
    if token is None:
        token = bytes.fromhex(args.token)
    log = _open_log(args, "handshake", mac,
                    {"hello_variant": args.hello_variant, "token": token.hex()})
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.event("connect failed", level="error", detail=str(exc))
        log.close()
        return 1

    with Session(sock, log, seq_mode=args.seq_mode) as session:
        print(f"\n  HELLO variant={args.hello_variant} token={token.hex()} delay={args.register_delay}s")
        heard = session.handshake(args.hello_variant, token, args.register_delay)
        if heard:
            print(f"  {len(session.all_frames)} frame(s) received -- this variant works.")
        else:
            print("  Silence. Try another --hello-variant or --token.")

        if args.listen:
            print(f"\n  Listening for {args.listen}s. Change settings on the phone to")
            print("  provoke notifications.")
            deadline = time.monotonic() + args.listen
            seen = len(session.all_frames)
            while time.monotonic() < deadline:
                time.sleep(0.5)
                if len(session.all_frames) > seen:
                    for frame in session.all_frames[seen:]:
                        print(f"\n  {annotate(frame)}")
                    seen = len(session.all_frames)

        print(f"\n  Total frames: {len(session.all_frames)}")
    log.close()
    return 0 if heard else 1


def cmd_get_sweep(args) -> int:
    mac = _resolve_mac(args)
    log = _open_log(args, "get-sweep", mac)
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.close()
        return 1

    with Session(sock, log, seq_mode=args.seq_mode) as session:
        if not session.handshake(args.hello_variant, known.REGISTER_TOKEN_DEFAULT, args.register_delay):
            print("  Handshake produced no response; the sweep will probably time out.")
        probes.get_sweep(session, log)
        if args.unknown_scan:
            probes.unknown_get_scan(session, log, lo=args.scan_from, hi=args.scan_to)
    log.close()
    return 0


def cmd_listen(args) -> int:
    mac = _resolve_mac(args)
    log = _open_log(args, "listen", mac)
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.close()
        return 1

    with Session(sock, log, seq_mode=args.seq_mode) as session:
        session.handshake(args.hello_variant, known.REGISTER_TOKEN_DEFAULT, args.register_delay)
        session.on_frame = lambda f: print(f"\n  {annotate(f)}")
        print(f"\n  Listening for {args.duration}s. Change settings on the phone or")
        print("  use a touch gesture -- every push shows up here.")
        try:
            time.sleep(args.duration)
        except KeyboardInterrupt:
            print("\n  stopped")
    log.close()
    return 0


def cmd_set_probe(args) -> int:
    tier = args.tier.upper()

    if not args.i_understand_the_risk:
        print("\n  Tiered SET probing changes real settings on the earbuds.")
        print("  Preconditions: battery above 30%, earbuds out of the case, the phone's")
        print("  buds settings page closed, no firmware update pending.")
        if tier == "A":
            print("\n  *** TIER A RINGS THE EARBUDS AT LOCATE VOLUME. ***")
            print("  Take them OUT OF YOUR EARS before running this. The find-device")
            print("  tone is designed to be heard across a room, not from inside your")
            print("  ear canal.")
        else:
            print("  Earbuds should be in your ears so you can hear each change.")
        print("\n  Re-run with --i-understand-the-risk to proceed.")
        return 2

    if tier not in probes.TIERS:
        print(f"  unknown tier {tier}; choose from {sorted(probes.TIERS)}")
        return 2

    mac = _resolve_mac(args)
    log = _open_log(args, f"set-probe-{tier}", mac, {"tier": tier})
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.close()
        return 1

    snap_path = Path(str(log.path).replace(".jsonl", ".restore.json"))
    with Session(sock, log, seq_mode=args.seq_mode) as session:
        session.handshake(args.hello_variant, known.REGISTER_TOKEN_DEFAULT, args.register_delay)
        print("\n  Taking a full read-only snapshot before touching anything...")
        probes.snapshot(session, snap_path)
        try:
            probes.TIERS[tier](session, log)
        except KeyboardInterrupt:
            print("\n  interrupted -- the tier's own finally block has restored its setting")
    log.close()
    print(f"\n  Snapshot kept at {snap_path}")
    return 0


def cmd_snapshot(args) -> int:
    """Read every responding command id and save it for later diffing."""
    mac = _resolve_mac(args)
    log = _open_log(args, "snapshot", mac, {"label": args.label})
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.close()
        return 1

    out = Path(args.out) if args.out else CAPTURES / f"snapshot_{args.label or _stamp()}.json"
    with Session(sock, log, seq_mode=args.seq_mode) as session:
        session.handshake(args.hello_variant, known.REGISTER_TOKEN_DEFAULT, args.register_delay)
        probes.snapshot_all(session, out, label=args.label or "")
    log.close()
    print(f"\n  Now change ONE setting on the phone, then run:")
    print(f"    python -m opbuds_probe snapshot --label after")
    print(f"    python -m opbuds_probe diff \"{out}\" <the new file>")
    return 0


def cmd_watch(args) -> int:
    """Poll the whole settings surface and report only what changes."""
    mac = _resolve_mac(args)
    log = _open_log(args, "watch", mac)
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.close()
        return 1

    with Session(sock, log, seq_mode=args.seq_mode) as session:
        session.handshake(args.hello_variant, known.REGISTER_TOKEN_DEFAULT, args.register_delay)
        if args.show_notifications:
            session.on_frame = lambda f: (
                print(f"\n  PUSH {annotate(f)}") if f.is_notification else None
            )
        try:
            probes.watch_loop(session, log, args.duration, interval=args.interval)
        except KeyboardInterrupt:
            print("\n  stopped")
    log.close()
    return 0


def _parse_raw_spec(spec: str) -> tuple[int, bytes]:
    """``0x010C`` or ``0x010C:0101`` -> (command, payload)."""
    command_text, _, payload_text = spec.partition(":")
    command = int(command_text, 16)
    payload = bytes.fromhex(payload_text.replace(" ", "")) if payload_text else b""
    return command, payload


def cmd_raw(args) -> int:
    """Send arbitrary frames and print the annotated replies.

    Reads are unrestricted. Writes go through the same allowlist as every other
    path here: the 0x04xx space is never fuzzed, so an unlisted setter is refused
    outright rather than merely warned about.
    """
    try:
        specs = [_parse_raw_spec(s) for s in args.specs]
    except ValueError as exc:
        print(f"  bad spec: {exc}")
        return 2

    for command, _ in specs:
        msg_type = (command >> 8) & 0xFF
        if msg_type == 0x04:
            if command not in known.SET_ALLOWLIST:
                print(f"  refusing 0x{command:04X}: not on the SET allowlist.")
                print("  An unknown setter could be a factory reset or a DFU entry point.")
                return 2
            if not args.i_understand_the_risk:
                print(f"  0x{command:04X} changes a real setting; pass --i-understand-the-risk.")
                return 2
        elif msg_type not in (0x01, 0x02, 0x05, 0x85):
            print(f"  refusing 0x{command:04X}: message type 0x{msg_type:02X} is not a known-safe type.")
            return 2

    mac = _resolve_mac(args)
    log = _open_log(args, "raw", mac, {"specs": args.specs})
    try:
        sock, channel = _connect(args, mac)
    except Exception as exc:
        print(f"  connect failed: {describe_error(exc)}")
        log.close()
        return 1

    with Session(sock, log, seq_mode=args.seq_mode) as session:
        session.handshake(args.hello_variant, known.REGISTER_TOKEN_DEFAULT, args.register_delay)
        print("\n  " + "-" * 62)
        for command, payload in specs:
            label = known.command_name(command)
            print(f"\n  TX 0x{command:04X}  {label}  payload={payload.hex(' ') or '(empty)'}")
            if (command >> 8) & 0xFF == 0x04:
                reply = session.send_set(command, payload)
            else:
                reply = session.request(command, payload, timeout=args.reply_timeout)
            if reply is None:
                print("  RX (timeout)")
                log.probe("raw", command, "timeout", payload=payload.hex())
            else:
                print(f"  RX {annotate(reply)}")
                log.probe("raw", command, "reply", payload=payload.hex(),
                          reply=reply.payload.hex())
            time.sleep(args.gap)
        if args.listen:
            print(f"\n  Listening a further {args.listen}s...")
            session.on_frame = lambda f: print(f"\n  {annotate(f)}")
            time.sleep(args.listen)
    log.close()
    return 0


def cmd_diff(args) -> int:
    """Compare two snapshots. Entirely offline -- no hardware needed."""
    before = json.loads(Path(args.before).read_text(encoding="utf-8"))
    after = json.loads(Path(args.after).read_text(encoding="utf-8"))
    print(f"\n  before: {before.get('label') or args.before}")
    print(f"  after : {after.get('label') or args.after}")
    probes.render_diff(probes.diff_snapshots(before, after))
    return 0

def cmd_decode(args) -> int:
    """Re-parse a capture offline. No hardware needed."""
    records = read_records(Path(args.path))
    parser = FrameParser()
    total = 0
    for rec in records:
        if rec.get("type") == "meta":
            print(f"\n  run: {rec.get('subcommand')} on {rec.get('mac')} "
                  f"({rec.get('operator_notes', 'no notes')})")
            print("  " + "-" * 62)
            continue
        if rec.get("type") != "io":
            continue
        raw = bytes.fromhex(rec["raw"])
        direction = rec["dir"]
        if direction == "tx":
            note = rec.get("note", "")
            print(f"\n  [{rec['t']:>8.3f}] TX {note}")
            print(f"    {raw.hex(' ')}")
            continue
        for frame in parser.feed(raw):
            total += 1
            print(f"\n  [{rec['t']:>8.3f}] RX")
            print(f"  {annotate(frame)}")
    print(f"\n  {total} frame(s) decoded from {len(records)} record(s)")
    if parser.resync_bytes:
        print(f"  {parser.resync_bytes} byte(s) discarded during resynchronisation")
    return 0


# --- argument parsing -----------------------------------------------------

def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(
        prog="python -m opbuds_probe",
        description="Discovery harness for the OnePlus/Oppo earbud control protocol.",
    )
    sub = ap.add_subparsers(dest="command", required=True)

    def add_common(sp, *, connect=True):
        sp.add_argument("--mac", help="device MAC; auto-detected from paired devices if omitted")
        sp.add_argument("--note", help="free-text note recorded in the capture metadata")
        if connect:
            sp.add_argument("--channel", type=int,
                            help="RFCOMM channel; omit to resolve the service UUID via SDP")
            sp.add_argument("--timeout", type=float, default=8.0, help="connect timeout (s)")
            sp.add_argument("--hello-variant", default="classic",
                            choices=sorted(known.HELLO_VARIANTS), help="HELLO packet form")
            sp.add_argument("--register-delay", type=float, default=1.2,
                            help="seconds between HELLO and REGISTER")
            sp.add_argument("--seq-mode", default="fixed", choices=("fixed", "rolling"),
                            help="per-command constant seq, or a rolling counter")

    sub.add_parser("devices", help="list paired Bluetooth devices").set_defaults(func=cmd_devices)

    sp = sub.add_parser("sdp", help="list RFCOMM services the device advertises")
    add_common(sp, connect=False)
    sp.add_argument("--uncached", action="store_true", default=True,
                    help="force a fresh SDP query (default)")
    sp.add_argument("--cached", dest="uncached", action="store_false",
                    help="use Windows' cached record instead")
    sp.set_defaults(func=cmd_sdp)

    sp = sub.add_parser("scan", help="brute-force which RFCOMM channels accept a connection")
    sp.add_argument("--mac")
    sp.add_argument("--note")
    sp.add_argument("--start", type=int, default=1)
    sp.add_argument("--end", type=int, default=30)
    sp.add_argument("--timeout", type=float, default=4.0)
    sp.add_argument("--gap", type=float, default=0.5, help="pause between attempts (s)")
    sp.set_defaults(func=cmd_scan)

    sp = sub.add_parser("handshake", help="try HELLO + REGISTER and log everything that comes back")
    add_common(sp)
    sp.add_argument("--token", default="default",
                    help="'default', 'zero', or raw hex such as b550a069")
    sp.add_argument("--listen", type=float, default=0.0,
                    help="keep listening this many seconds afterwards")
    sp.set_defaults(func=cmd_handshake)

    sp = sub.add_parser("get-sweep", help="send every known read request and dump the replies")
    add_common(sp)
    sp.add_argument("--unknown-scan", action="store_true",
                    help="also sweep undocumented request ids (still read-only)")
    sp.add_argument("--scan-from", type=lambda s: int(s, 0), default=0x00)
    sp.add_argument("--scan-to", type=lambda s: int(s, 0), default=0x30)
    sp.set_defaults(func=cmd_get_sweep)

    sp = sub.add_parser("listen", help="stay connected and print pushed notifications")
    add_common(sp)
    sp.add_argument("--duration", type=float, default=300.0)
    sp.set_defaults(func=cmd_listen)

    sp = sub.add_parser("set-probe", help="tiered SET probing with snapshot and restore")
    add_common(sp)
    sp.add_argument("--tier", required=True, help="A (find device), B (ANC), C (EQ presets)")
    sp.add_argument("--i-understand-the-risk", action="store_true")
    sp.set_defaults(func=cmd_set_probe)

    sp = sub.add_parser("snapshot",
                        help="read every command id and save it, for differential analysis")
    add_common(sp)
    sp.add_argument("--label", default="", help="a name for this snapshot, e.g. 'anc-off'")
    sp.add_argument("--out", help="output path (default: captures/snapshot_<label>.json)")
    sp.set_defaults(func=cmd_snapshot)

    sp = sub.add_parser("watch",
                        help="poll every setting and print only what changes, live")
    add_common(sp)
    sp.add_argument("--duration", type=float, default=300.0)
    sp.add_argument("--interval", type=float, default=1.5, help="pause between cycles (s)")
    sp.add_argument("--show-notifications", action="store_true",
                    help="also print unsolicited pushes as they arrive")
    sp.set_defaults(func=cmd_watch)

    sp = sub.add_parser("raw", help="send arbitrary frames, e.g. 0x010C or 0x010C:0101")
    add_common(sp)
    sp.add_argument("specs", nargs="+", metavar="CMD[:PAYLOADHEX]",
                    help="16-bit command, optionally ':' then payload hex")
    sp.add_argument("--gap", type=float, default=1.0, help="pause between frames (s)")
    sp.add_argument("--reply-timeout", type=float, default=3.0)
    sp.add_argument("--listen", type=float, default=0.0,
                    help="keep listening this many seconds afterwards")
    sp.add_argument("--i-understand-the-risk", action="store_true",
                    help="required for any 0x04xx setter (allowlisted ones only)")
    sp.set_defaults(func=cmd_raw)

    sp = sub.add_parser("diff", help="compare two snapshots to see which command changed")
    sp.add_argument("before")
    sp.add_argument("after")
    sp.set_defaults(func=cmd_diff)
    sp = sub.add_parser("decode", help="re-parse a capture offline, no hardware needed")
    sp.add_argument("path")
    sp.set_defaults(func=cmd_decode)

    return ap


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except KeyboardInterrupt:
        print("\n  interrupted")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
