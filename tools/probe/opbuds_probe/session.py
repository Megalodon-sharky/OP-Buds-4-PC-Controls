"""Connected session: reader thread, handshake, request/reply correlation."""

from __future__ import annotations

import socket
import threading
import time
from typing import Callable

from . import known
from .btsock import describe_error
from .frames import Frame, FrameParser, MsgType, encode_cmd
from .proto_log import ProtocolLog


class ConnectionLost(OSError):
    """The RFCOMM link dropped mid-session.

    Routine, not exceptional: it happens whenever the earbuds go back in the
    case or out of range. Probe loops catch this and report partial results
    rather than dying with a traceback.
    """


class Session:
    """One live RFCOMM session with the earbuds.

    The reader thread is started before HELLO is sent and kept running through
    the register delay, because the earbuds push an unsolicited state broadcast
    immediately after REGISTER. That broadcast is the single most information
    dense frame available and is easy to miss.

    Correlation here keys on (expected_reply_type, cmd_id) alone rather than
    also on seq. The probe serialises its requests, so that is sufficient and it
    survives a device that does not echo seq the way we expect. The C# client
    issues requests concurrently and must include seq in the key.
    """

    def __init__(
        self,
        sock: socket.socket,
        log: ProtocolLog | None = None,
        seq_mode: str = "fixed",
    ) -> None:
        self._sock = sock
        self._log = log
        self._parser = FrameParser()
        self._seq_mode = seq_mode
        self._rolling_seq = 0
        self._pending: dict[tuple[int, int], list[Frame]] = {}
        self._pending_evt: dict[tuple[int, int], threading.Event] = {}
        self._lock = threading.Lock()
        self._send_lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.notifications: list[Frame] = []
        self.all_frames: list[Frame] = []
        self.on_frame: Callable[[Frame], None] | None = None

    # -- lifecycle ---------------------------------------------------------

    def start(self) -> None:
        self._thread = threading.Thread(target=self._read_loop, name="buds-reader", daemon=True)
        self._thread.start()

    def close(self) -> None:
        self._stop.set()
        try:
            self._sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            self._sock.close()
        except OSError:
            pass
        if self._thread is not None:
            self._thread.join(timeout=2.0)

    def __enter__(self) -> "Session":
        self.start()
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    # -- reader ------------------------------------------------------------

    def _read_loop(self) -> None:
        self._sock.settimeout(0.5)
        while not self._stop.is_set():
            try:
                chunk = self._sock.recv(4096)
            except socket.timeout:
                continue
            except OSError:
                break
            if not chunk:
                if self._log:
                    self._log.event("socket closed by peer", level="warn")
                break
            if self._log:
                self._log.io("rx", chunk)
            for frame in self._parser.feed(chunk):
                self._dispatch(frame)

    def _dispatch(self, frame: Frame) -> None:
        self.all_frames.append(frame)
        if self._log:
            decode = self._decode(frame)
            self._log.frame("rx", frame, decode=decode)
        if self.on_frame:
            self.on_frame(frame)

        key = (frame.msg_type, frame.cmd_id)
        with self._lock:
            evt = self._pending_evt.get(key)
            if evt is not None:
                self._pending.setdefault(key, []).append(frame)
                evt.set()
                return
        self.notifications.append(frame)

    @staticmethod
    def _decode(frame: Frame):
        """Best-effort semantic decode. Only what we are confident about."""
        if frame.msg_type == MsgType.REPLY and frame.cmd_id == (known.BATTERY_GET & 0xFF):
            battery = known.decode_battery(frame.payload)
            if battery:
                return {"battery": battery}
        return None

    # -- sending -----------------------------------------------------------

    def _next_seq(self, command: int) -> int:
        if self._seq_mode == "rolling":
            self._rolling_seq = (self._rolling_seq + 1) & 0xFF
            return self._rolling_seq
        return known.fixed_seq_for(command)

    def send_raw(self, data: bytes, note: str | None = None) -> None:
        with self._send_lock:
            if self._log:
                self._log.io("tx", data, note=note)
            try:
                self._sock.sendall(data)
            except OSError as exc:
                self._stop.set()
                if self._log:
                    self._log.event("link lost while sending", level="warn", detail=str(exc))
                raise ConnectionLost(
                    f"the earbuds disconnected mid-session ({describe_error(exc)}). "
                    f"They are probably back in the case or out of range."
                ) from exc

    def send(self, command: int, payload: bytes = b"", note: str | None = None) -> int:
        seq = self._next_seq(command)
        self.send_raw(encode_cmd(command, seq, payload), note=note or known.command_name(command))
        return seq

    def request(self, command: int, payload: bytes = b"", timeout: float = 3.0) -> Frame | None:
        """Send a request and wait for its reply. Returns None on timeout."""
        msg_type = (command >> 8) & 0xFF
        expected = MsgType.SET_ACK if msg_type == MsgType.SET else MsgType.REPLY
        key = (int(expected), command & 0xFF)

        evt = threading.Event()
        with self._lock:
            self._pending_evt[key] = evt
            self._pending.pop(key, None)
        try:
            self.send(command, payload)
            if not evt.wait(timeout):
                return None
            with self._lock:
                frames = self._pending.get(key) or []
            return frames[0] if frames else None
        finally:
            with self._lock:
                self._pending_evt.pop(key, None)
                self._pending.pop(key, None)

    def send_set(self, command: int, payload: bytes, timeout: float = 3.0) -> Frame | None:
        """Send a SET, enforcing the allowlist.

        Nothing outside known.SET_ALLOWLIST is ever transmitted with message
        type 0x04. An unknown setter could be a factory reset or a DFU entry
        point, so that space is never fuzzed.
        """
        if ((command >> 8) & 0xFF) != MsgType.SET:
            raise ValueError(f"0x{command:04X} is not a SET command")
        if command not in known.SET_ALLOWLIST:
            raise PermissionError(
                f"0x{command:04X} is not on the SET allowlist -- refusing to transmit. "
                f"Add it to known.SET_ALLOWLIST deliberately if you are sure."
            )
        return self.request(command, payload, timeout=timeout)

    # -- handshake ---------------------------------------------------------

    def handshake(
        self,
        hello_variant: str = "classic",
        token: bytes = known.REGISTER_TOKEN_DEFAULT,
        register_delay: float = 1.2,
        settle: float = 1.5,
    ) -> bool:
        """HELLO, wait, REGISTER, wait. Returns whether any frame came back.

        Both reference implementations treat this as fire-and-forget with fixed
        delays and never validate the responses. We do the same, but report
        whether anything was heard at all, because silence is the signal that a
        handshake variant is wrong.
        """
        hello = known.HELLO_VARIANTS.get(hello_variant)
        if hello is None:
            raise ValueError(f"unknown hello variant {hello_variant!r}; "
                             f"choose from {sorted(known.HELLO_VARIANTS)}")

        before = len(self.all_frames)
        self.send_raw(hello, note=f"HELLO ({hello_variant})")
        time.sleep(register_delay)

        seq = known.fixed_seq_for(known.REGISTER)
        self.send_raw(
            encode_cmd(known.REGISTER, seq, known.register_payload(token)),
            note=f"REGISTER (token {token.hex()})",
        )
        time.sleep(settle)

        heard = len(self.all_frames) - before
        if self._log:
            self._log.event(
                f"handshake complete, {heard} frame(s) received",
                level="info" if heard else "warn",
                hello_variant=hello_variant,
                token=token.hex(),
                register_delay=register_delay,
            )
        return heard > 0
