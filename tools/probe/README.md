# opbuds_probe

Discovery harness for the OnePlus/Oppo earbud control protocol. Research tooling,
not a shipped product. Python 3.10+, standard library only, Windows only.

It exists because nobody has publicly reverse-engineered the OnePlus Buds 4. Every
constant currently in `known.py` is extrapolated from the Buds Pro 3 or from
Oppo/Realme models, and this tool is how those guesses get confirmed or refuted.

Keep it after the C# app works. It has no dependencies, it re-earns its keep on
every firmware update, and it is the independent oracle when the C# stack starts
misbehaving.

## Layout

| File | Role |
|---|---|
| `frames.py` | Frame encode/decode and the incremental parser. Pure, no I/O. |
| `known.py` | Command codes, enums, per-model constants, the SET allowlist. |
| `btsock.py` | RFCOMM sockets: by channel via the stdlib, or by service UUID via ctypes. |
| `devices.py` | Paired-device enumeration and SDP service queries. |
| `session.py` | Reader thread, handshake, request/reply correlation. |
| `probes.py` | Read-only sweeps and the tiered SET probes. |
| `proto_log.py` | JSONL capture log. |
| `__main__.py` | CLI. |

`selftest.py` (one level up) checks the codec against the documented Buds Pro 3
constants byte-for-byte and fuzzes the parser. Run it after touching `frames.py`:

```powershell
python selftest.py
```

## Two notes that will save you an hour

**WinRT needs Windows PowerShell 5.1.** `devices.py` shells out to
`powershell.exe`, not `powershell`, because PowerShell 7 does not project WinRT
types and the SDP query fails with "Unable to find type
[Windows.Devices.Bluetooth.BluetoothDevice]".

**Uncached SDP needs a live connection.** On a disconnected device an uncached
query returns nothing at all, while the cached record persists but reports channel
0. `sdp` tries uncached first and falls back, telling you which it used.

## The capture format

Each run writes `captures/<timestamp>_<mac>_<subcommand>.jsonl`. Two record types
carry the data, and the split is deliberate:

- `io` -- ground truth. Raw chunks exactly as `send`/`recv` saw them, never
  re-split into frames, so real wire chunk boundaries survive. The C#
  `ReplayTransport` reads only these, which turns recorded boundaries into free
  fuzz coverage for the frame reader.
- `frame` -- interpretation. Regenerable offline with `decode`, and revisable as
  understanding improves without touching hardware again.

Plus `meta` (one per run), `probe` (one attempt's outcome, including timeouts,
which are results too) and `event`.
