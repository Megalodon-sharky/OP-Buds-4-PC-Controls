"""Frame codec for the OnePlus/Oppo ("OPO") control protocol.

Wire format::

    AA <len> 00 00 <cmdId> <msgType> <seq> <plenLo> <plenHi> [payload...]

``len`` is a base-128 VARINT (LEB128, little-endian groups of 7 bits, high bit
set on every byte but the last). It counts every byte after itself, so::

    total  = 1 + len(varint) + len
    header = 1 + len(varint) + 7
    payload_length == total - header

That gives the one integrity check the protocol offers; there is no checksum or
CRC anywhere. Resynchronisation is by scanning for the 0xAA magic byte.

The varint matters: payloads on this device really do exceed 127 bytes. Command
0x011F replies with a 299-byte payload in a 309-byte frame whose length field is
``b2 02``. A single-byte reading of that field predicts 180 bytes and silently
mis-frames the stream, which is how that command was first mistaken for one that
never answers. Audited across every capture, the varint model parses 85 of 85
received frames with zero resynchronisations.

Note on bytes 4-5: the published reference implementations describe these as a
single little-endian 16-bit command code. That model breaks on REGISTER, whose
bytes are ``00 85`` and would decode as "command 0x8500". Treating byte 4 as the
command id and byte 5 as the message type fits every observed frame. The familiar
``0x0106`` / ``0x8106`` labels are just ``(msg_type << 8) | cmd_id`` and remain
valid as names.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import IntEnum

MAGIC = 0xAA
#: Header size when the length varint occupies a single byte (payload < 121).
HEADER_LEN = 9
#: Fields after the length varint: 00 00, cmdId, msgType, seq, plenLo, plenHi.
FIXED_HEADER = 7
MAX_VARINT_BYTES = 4


class MsgType(IntEnum):
    """Message type, carried in byte 5."""

    REQUEST = 0x01
    SET = 0x04
    NOTIFY = 0x02
    NOTIFY_ALT = 0x05
    REPLY = 0x81
    SET_ACK = 0x84
    REGISTER = 0x85

    @classmethod
    def name_of(cls, value: int) -> str:
        try:
            return cls(value).name
        except ValueError:
            return f"0x{value:02X}"


#: Request type -> the reply type the earbuds answer with.
EXPECTED_REPLY = {
    MsgType.REQUEST: MsgType.REPLY,
    MsgType.SET: MsgType.SET_ACK,
    MsgType.REGISTER: 0x05,
}


@dataclass(frozen=True)
class Frame:
    """One decoded protocol frame."""

    cmd_id: int
    msg_type: int
    seq: int
    payload: bytes = b""
    raw: bytes = field(default=b"", repr=False)

    @property
    def command(self) -> int:
        """The conventional 16-bit label, e.g. 0x0106 for a battery request."""
        return (self.msg_type << 8) | self.cmd_id

    @property
    def is_reply(self) -> bool:
        return self.msg_type in (MsgType.REPLY, MsgType.SET_ACK)

    @property
    def is_notification(self) -> bool:
        return self.msg_type in (MsgType.NOTIFY, MsgType.NOTIFY_ALT)

    @property
    def status(self) -> int | None:
        """Status byte of an ack/reply. 0 means success."""
        if self.is_reply and self.payload:
            return self.payload[0]
        return None

    @property
    def ok(self) -> bool:
        s = self.status
        return s is None or s == 0

    def __str__(self) -> str:
        return (
            f"cmd=0x{self.command:04X} id=0x{self.cmd_id:02X} "
            f"type={MsgType.name_of(self.msg_type)} seq={self.seq} "
            f"plen={len(self.payload)} payload={self.payload.hex(' ') or '-'}"
        )


def encode_varint(value: int) -> bytes:
    """LEB128: 7 bits per byte, high bit set on every byte except the last."""
    if value < 0:
        raise ValueError(f"varint cannot be negative: {value}")
    out = bytearray()
    while True:
        chunk = value & 0x7F
        value >>= 7
        if value:
            out.append(chunk | 0x80)
        else:
            out.append(chunk)
            return bytes(out)


def decode_varint(buf: bytes, offset: int = 0) -> tuple[int, int] | None:
    """Return ``(value, bytes_consumed)``, or None if more data is needed."""
    value = shift = 0
    for i in range(MAX_VARINT_BYTES):
        pos = offset + i
        if pos >= len(buf):
            return None
        byte = buf[pos]
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value, i + 1
        shift += 7
    raise ValueError("length varint longer than 4 bytes - stream is desynchronised")


def encode(cmd_id: int, msg_type: int, seq: int, payload: bytes = b"") -> bytes:
    """Build a frame. ``len`` is 7 header bytes after itself plus the payload."""
    if not 0 <= cmd_id <= 0xFF:
        raise ValueError(f"cmd_id out of range: {cmd_id}")
    if not 0 <= msg_type <= 0xFF:
        raise ValueError(f"msg_type out of range: {msg_type}")
    plen = len(payload)
    if plen > 0xFFFF:
        raise ValueError(f"payload too long: {plen}")
    body = bytes(
        (0x00, 0x00, cmd_id, msg_type, seq & 0xFF, plen & 0xFF, (plen >> 8) & 0xFF)
    ) + payload
    return bytes((MAGIC,)) + encode_varint(len(body)) + body


def encode_cmd(command: int, seq: int, payload: bytes = b"") -> bytes:
    """Build a frame from a conventional 16-bit label such as 0x0106."""
    return encode(command & 0xFF, (command >> 8) & 0xFF, seq, payload)


class DecodeResult(IntEnum):
    OK = 0
    NEED_MORE = 1
    RESYNC = 2


def try_decode(buf: bytes) -> tuple[DecodeResult, Frame | None, int]:
    """Attempt to decode one frame from the front of ``buf``.

    Returns ``(result, frame, consumed)``. On RESYNC, ``consumed`` is how many
    bytes to discard before trying again.
    """
    if not buf:
        return DecodeResult.NEED_MORE, None, 0

    if buf[0] != MAGIC:
        skip = buf.find(MAGIC)
        return DecodeResult.RESYNC, None, len(buf) if skip < 0 else skip

    try:
        parsed = decode_varint(buf, 1)
    except ValueError:
        return DecodeResult.RESYNC, None, 1
    if parsed is None:
        return DecodeResult.NEED_MORE, None, 0
    body_len, varint_len = parsed

    header = 1 + varint_len + FIXED_HEADER
    total = 1 + varint_len + body_len
    if body_len < FIXED_HEADER:
        # Too small to hold even the fixed header - a 0xAA inside a payload.
        return DecodeResult.RESYNC, None, 1
    if len(buf) < total:
        return DecodeResult.NEED_MORE, None, 0

    base = 1 + varint_len
    plen = buf[base + 5] | (buf[base + 6] << 8)
    if plen != total - header:
        # No checksum exists, so this identity is the only integrity check.
        return DecodeResult.RESYNC, None, 1

    frame = Frame(
        cmd_id=buf[base + 2],
        msg_type=buf[base + 3],
        seq=buf[base + 4],
        payload=bytes(buf[header:total]),
        raw=bytes(buf[:total]),
    )
    return DecodeResult.OK, frame, total


class FrameParser:
    """Incremental parser: feed it arbitrary chunks, get whole frames out.

    Handles partial frames, several frames in one chunk, and stray bytes between
    frames. Stray bytes are not hypothetical here: the HELLO packet used by every
    reference implementation carries a trailing 0x12 that its own length field
    does not account for.
    """

    def __init__(self, max_buffer: int = 4096) -> None:
        self._buf = bytearray()
        self._max_buffer = max_buffer
        self.resync_bytes = 0

    def feed(self, chunk: bytes) -> list[Frame]:
        self._buf.extend(chunk)
        frames: list[Frame] = []
        while True:
            result, frame, consumed = try_decode(bytes(self._buf))
            if result is DecodeResult.OK and frame is not None:
                del self._buf[:consumed]
                frames.append(frame)
            elif result is DecodeResult.RESYNC:
                if consumed == 0:
                    consumed = 1
                self.resync_bytes += consumed
                del self._buf[:consumed]
            else:
                break

        if len(self._buf) > self._max_buffer:
            self.resync_bytes += len(self._buf)
            self._buf.clear()
        return frames

    @property
    def pending(self) -> bytes:
        return bytes(self._buf)


def hexdump(data: bytes, width: int = 16, indent: str = "  ") -> str:
    """Classic offset / hex / ascii dump."""
    lines = []
    for off in range(0, len(data), width):
        row = data[off : off + width]
        hexpart = " ".join(f"{b:02x}" for b in row).ljust(width * 3 - 1)
        asciipart = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        lines.append(f"{indent}{off:04x}  {hexpart}  |{asciipart}|")
    return "\n".join(lines)


def annotate(frame: Frame) -> str:
    """Render a frame with each header field labelled."""
    from . import known

    label = known.command_name(frame.command)
    parts = [
        f"AA          magic",
        f"{encode_varint(len(frame.raw) - 1 - len(encode_varint(len(frame.raw)))).hex()}          len varint ({len(frame.raw)} bytes total)",
        f"00 00       reserved",
        f"{frame.cmd_id:02X}          cmd id",
        f"{frame.msg_type:02X}          msg type = {MsgType.name_of(frame.msg_type)}",
        f"{frame.seq:02X}          seq",
        f"{len(frame.payload) & 0xFF:02X} {(len(frame.payload) >> 8) & 0xFF:02X}       payload len = {len(frame.payload)}",
    ]
    head = f"0x{frame.command:04X}  {label}"
    body = "\n".join("    " + p for p in parts)
    tail = hexdump(frame.payload, indent="    ") if frame.payload else "    (no payload)"
    return f"{head}\n{body}\n  payload:\n{tail}"
