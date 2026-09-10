"""RFCOMM socket helpers for Windows.

Two ways in:

``connect_channel``
    Python's stdlib. ``socket.AF_BLUETOOTH`` + ``BTPROTO_RFCOMM`` with an
    address of exactly ``(mac_string, channel_int)``. Requires knowing the
    channel number -- the stdlib cannot do an SDP lookup, and passing port 0
    returns WSAEADDRNOTAVAIL.

``connect_uuid``
    ctypes. Builds the 30-byte SOCKADDR_BTH by hand with ``port = 0`` and the
    service class GUID filled in, which makes *Windows* perform the SDP
    resolution. This mirrors what the C# app will do via 32feet, and is the only
    route in Python that does not need the channel up front.

SOCKADDR_BTH layout (ws2bth.h), 30 bytes total::

    offset 0  : USHORT   addressFamily   = AF_BTH (32)
    offset 2  : ULONGLONG btAddr         48-bit address, little-endian
    offset 10 : GUID     serviceClassId  16 bytes, Windows GUID memory layout
    offset 26 : ULONG    port            RFCOMM channel, or 0 to use the GUID
"""

from __future__ import annotations

import ctypes
import select
import socket
import uuid as _uuid

AF_BTH = 32
BTHPROTO_RFCOMM = 3
SOCKADDR_BTH_SIZE = 30


def parse_mac(mac: str) -> int:
    """"12:34:56:78:9A:BC" (or with dashes, or bare) -> 0x123456789ABC."""
    cleaned = mac.replace(":", "").replace("-", "").replace(".", "").strip()
    if len(cleaned) != 12:
        raise ValueError(f"not a 48-bit MAC: {mac!r}")
    return int(cleaned, 16)


def format_mac(value: int) -> str:
    raw = f"{value:012X}"
    return ":".join(raw[i : i + 2] for i in range(0, 12, 2))


def build_sockaddr_bth(mac: str, channel: int = 0, service_uuid: str | None = None) -> bytes:
    """Serialise SOCKADDR_BTH. Set either ``channel`` or ``service_uuid``."""
    buf = bytearray(SOCKADDR_BTH_SIZE)
    buf[0:2] = AF_BTH.to_bytes(2, "little")
    buf[2:10] = parse_mac(mac).to_bytes(8, "little")
    if service_uuid:
        # bytes_le is exactly the Windows GUID in-memory layout: Data1 and Data2
        # and Data3 little-endian, Data4 in order.
        buf[10:26] = _uuid.UUID(service_uuid).bytes_le
    buf[26:30] = int(channel).to_bytes(4, "little")
    return bytes(buf)


def connect_channel(mac: str, channel: int, timeout: float = 5.0) -> socket.socket:
    """Connect to an explicit RFCOMM channel using the stdlib."""
    sock = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    sock.settimeout(timeout)
    try:
        sock.connect((mac, channel))
    except Exception:
        sock.close()
        raise
    return sock


#: Winsock reports an in-progress non-blocking connect as WSAEWOULDBLOCK rather
#: than EINPROGRESS the way POSIX does.
WSAEWOULDBLOCK = 10035
WSAEALREADY = 10037
WSAEISCONN = 10056


def connect_uuid(mac: str, service_uuid: str, timeout: float = 8.0) -> tuple[socket.socket, int | None]:
    """Connect by service UUID, letting Windows resolve the channel via SDP.

    Returns ``(socket, resolved_channel)``; the channel is None if Windows does
    not report it back through getpeername.

    Setting a Python timeout on a socket puts it into non-blocking mode, so a
    raw ws2_32.connect on it returns WSAEWOULDBLOCK immediately rather than
    completing. The connect therefore has to be driven by hand: issue it, wait
    for the socket to become writable, then read SO_ERROR to find out whether it
    actually succeeded.
    """
    sock = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    sock.setblocking(False)

    addr = build_sockaddr_bth(mac, channel=0, service_uuid=service_uuid)
    ws2 = ctypes.WinDLL("ws2_32", use_last_error=True)
    buf = ctypes.create_string_buffer(addr, SOCKADDR_BTH_SIZE)

    rc = ws2.connect(ctypes.c_void_p(sock.fileno()), buf, SOCKADDR_BTH_SIZE)
    if rc != 0:
        err = ctypes.get_last_error()
        if err == WSAEISCONN:
            pass  # already done
        elif err in (WSAEWOULDBLOCK, WSAEALREADY):
            _, writable, failed = select.select([], [sock], [sock], timeout)
            if failed:
                so_err = sock.getsockopt(socket.SOL_SOCKET, socket.SO_ERROR)
                sock.close()
                raise OSError(so_err, f"connect by UUID failed (WSA error {so_err})")
            if not writable:
                sock.close()
                raise TimeoutError(
                    f"connect by UUID timed out after {timeout}s -- Windows may not have "
                    f"resolved the service, or the channel is already in use"
                )
            so_err = sock.getsockopt(socket.SOL_SOCKET, socket.SO_ERROR)
            if so_err != 0:
                sock.close()
                raise OSError(so_err, f"connect by UUID failed (WSA error {so_err})")
        else:
            sock.close()
            raise OSError(err, f"connect by UUID failed (WSA error {err})")

    sock.setblocking(True)
    sock.settimeout(timeout)

    channel = None
    try:
        peer = sock.getpeername()
        if isinstance(peer, tuple) and len(peer) >= 2:
            channel = int(peer[1])
    except Exception:
        pass
    return sock, channel

#: Winsock errors worth distinguishing during a channel scan.
WSA_ERRORS = {
    10013: "access denied",
    10048: "address in use",
    10049: "address not available",
    10050: "network down (radio off?)",
    10060: "timed out",
    10035: "would block (non-blocking connect still in progress)",
    10061: "connection refused (nothing listening on this channel)",
    10064: "host down (earbuds asleep or in case)",
}


def describe_error(exc: BaseException) -> str:
    code = getattr(exc, "winerror", None) or getattr(exc, "errno", None)
    if code in WSA_ERRORS:
        return f"{WSA_ERRORS[code]} ({code})"
    return f"{type(exc).__name__}: {exc}"
