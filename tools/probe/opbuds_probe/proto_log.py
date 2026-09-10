"""JSONL protocol log -- the contract between the probe and every later stage.

Two record classes exist on purpose:

``io``
    Ground truth. Raw chunks exactly as ``send``/``recv`` saw them, never
    re-split into frames, so real wire chunk boundaries are preserved. The C#
    ``ReplayTransport`` consumes only these, which turns recorded boundaries
    into free fuzz coverage for the frame reader.

``frame``
    Interpretation. Regenerable offline by the ``decode`` subcommand and
    revisable as understanding improves, without touching hardware again.

Also emitted: ``meta`` (one per run, first line), ``probe`` (the outcome of one
probe attempt, including timeouts, which are results too) and ``event``.
"""

from __future__ import annotations

import json
import time
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1


class ProtocolLog:
    def __init__(self, path: Path, meta: dict[str, Any] | None = None) -> None:
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self._fh = self.path.open("w", encoding="utf-8", newline="\n")
        self._t0 = time.monotonic()
        self._lines = 0
        record = {"type": "meta", "schema": SCHEMA_VERSION, "tool": "opbuds_probe/0.1"}
        record.update(meta or {})
        self._write(record)

    # -- internals ---------------------------------------------------------

    def _write(self, record: dict[str, Any]) -> int:
        self._fh.write(json.dumps(record, separators=(",", ":")) + "\n")
        self._fh.flush()
        self._lines += 1
        return self._lines

    def _t(self) -> float:
        return round(time.monotonic() - self._t0, 4)

    # -- record types ------------------------------------------------------

    def io(self, direction: str, raw: bytes, note: str | None = None) -> None:
        """Record a raw chunk. ``direction`` is "tx" or "rx"."""
        rec: dict[str, Any] = {"type": "io", "t": self._t(), "dir": direction, "raw": raw.hex()}
        if note:
            rec["note"] = note
        self._write(rec)

    def frame(self, direction: str, frame: Any, decode: Any = None, confidence: str = "medium") -> None:
        from . import known

        rec = {
            "type": "frame",
            "t": self._t(),
            "dir": direction,
            "cmd": f"0x{frame.command:04X}",
            "name": known.command_name(frame.command),
            "id": f"0x{frame.cmd_id:02X}",
            "mtype": f"0x{frame.msg_type:02X}",
            "seq": frame.seq,
            "plen": len(frame.payload),
            "payload": frame.payload.hex(),
        }
        if decode is not None:
            rec["decode"] = decode
            rec["confidence"] = confidence
        self._write(rec)

    def probe(self, op: str, command: int, result: str, **extra: Any) -> None:
        rec = {"type": "probe", "t": self._t(), "op": op, "cmd": f"0x{command:04X}", "result": result}
        rec.update(extra)
        self._write(rec)

    def event(self, msg: str, level: str = "info", **extra: Any) -> None:
        rec = {"type": "event", "t": self._t(), "level": level, "msg": msg}
        rec.update(extra)
        self._write(rec)

    # -- lifecycle ---------------------------------------------------------

    def close(self) -> None:
        if not self._fh.closed:
            self._fh.close()

    def __enter__(self) -> "ProtocolLog":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()


def read_records(path: Path) -> list[dict[str, Any]]:
    """Load a JSONL log, skipping blank and malformed lines."""
    out = []
    with Path(path).open("r", encoding="utf-8") as fh:
        for lineno, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError:
                print(f"  warning: line {lineno} is not valid JSON, skipped")
    return out


def default_name(mac: str, subcommand: str, stamp: str) -> str:
    """captures/<stamp>_<mac>_<subcommand>.jsonl"""
    safe_mac = mac.replace(":", "").lower()
    return f"{stamp}_{safe_mac}_{subcommand}.jsonl"
