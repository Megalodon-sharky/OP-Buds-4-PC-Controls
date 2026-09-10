import sys
from opbuds_probe.frames import encode, encode_cmd, try_decode, FrameParser, DecodeResult
from opbuds_probe import known

fails = []
def check(label, got, want):
    if got != want:
        fails.append(f"{label}: got {got!r} want {want!r}")
    print(f"  {'ok ' if got == want else 'FAIL'} {label}")

# 1. Encode matches the documented Buds Pro 3 constants byte for byte.
check("battery request", encode_cmd(known.BATTERY_GET, 0x25).hex(), "aa07000006012500 00".replace(" ",""))
check("anc on", encode_cmd(known.ANC_SET, 0x42, bytes((1,1,2))).hex(), "aa0a0000040442030001 0102".replace(" ",""))
check("anc high", encode_cmd(known.ANC_SET, 0x42, bytes((1,1,0x10,0))).hex(), "aa0b000004044204000101 1000".replace(" ",""))
check("register", encode_cmd(known.REGISTER, 0x41, known.register_payload()).hex(), "aa0c0000008541050000b550a069")
check("eq preset", encode_cmd(known.EQ_PRESET_SET, 0x00, bytes((0x03,))).hex(), "aa080000060400010003")
check("basswave lvl", encode_cmd(known.BASSWAVE_LEVEL_SET, 0, bytes((0xFB,0x05,0xFE))).hex(), "aa0a00001b0400030 0fb05fe".replace(" ",""))

# 2. HELLO variants are self-consistent where they claim to be.
r, f, n = try_decode(known.HELLO_NO_TRAILER)
check("hello no-trailer decodes", r, DecodeResult.OK)
r, f, n = try_decode(known.HELLO_LEN_FIXED)
check("hello len-fixed decodes", r, DecodeResult.OK)
check("hello len-fixed payload", f.payload.hex(), "12")

# 3. The classic HELLO's trailing 0x12 really is outside the frame.
r, f, n = try_decode(known.HELLO_CLASSIC)
check("hello classic consumes 9 of 10", (r, n, len(known.HELLO_CLASSIC)), (DecodeResult.OK, 9, 10))

# 4. Round trip.
for cid in (0x00, 0x06, 0x1B, 0xFF):
    for mt in (0x01, 0x04, 0x81, 0x84):
        for pl in (b"", b"\x00", bytes(range(40))):
            raw = encode(cid, mt, 0x37, pl)
            r, f, n = try_decode(raw)
            if not (r == DecodeResult.OK and f.cmd_id == cid and f.msg_type == mt and f.payload == pl and n == len(raw)):
                fails.append(f"roundtrip {cid:02x}/{mt:02x}/{len(pl)}")
print(f"  {'ok ' if not any('roundtrip' in x for x in fails) else 'FAIL'} round trip (48 cases)")

# 5. FrameParser: byte-at-a-time vs one blob vs garbage-injected must agree.
stream = encode_cmd(known.BATTERY_GET, 0x25) + encode_cmd(known.ANC_SET, 0x42, bytes((1,1,2))) + encode_cmd(known.EQ_PRESET_GET, 0)
def collect(chunks):
    p = FrameParser(); out = []
    for c in chunks: out.extend(p.feed(c))
    return [f.raw.hex() for f in out]
blob = collect([stream])
onebyte = collect([bytes([b]) for b in stream])
garbage = collect([b"\x00\xff\xaa\x01" + stream])
trailer = collect([known.HELLO_CLASSIC + stream])
check("parser: blob == byte-at-a-time", onebyte, blob)
check("parser: survives leading garbage", garbage, blob)
check("parser: survives HELLO trailer", trailer[1:], blob)
check("parser: found 3 frames", len(blob), 3)

# 6. Truncated input yields nothing and does not raise.
check("parser: truncated is silent", collect([stream[:-3]]), blob[:2])

# 7. Battery decode.
check("battery decode", known.decode_battery(bytes.fromhex("00030154 02d2 0364".replace(" ",""))),
      [{"id":1,"name":"left","percent":84,"charging":False},
       {"id":2,"name":"right","percent":82,"charging":True},
       {"id":3,"name":"case","percent":100,"charging":False}])

# 8. Command naming covers reply forms.
check("name reply", known.command_name(0x8106), "BATTERY_GET reply")
check("name ack", known.command_name(0x8404), "ANC_SET ack")

# 9. Varint length field. The device really does send payloads over 127 bytes:
#    command 0x011F replies with 299 bytes in a 309-byte frame. A single-byte
#    length reading predicts 180 and silently mis-frames the stream.
from opbuds_probe.frames import encode_varint, decode_varint
check("varint encode 306", encode_varint(306).hex(), "b202")
check("varint decode b2 02", decode_varint(bytes.fromhex("b202")), (306, 2))
check("varint encode 7 stays 1 byte", encode_varint(7).hex(), "07")
check("varint decode needs more", decode_varint(bytes.fromhex("b2")), None)

big = encode(0x1F, 0x81, 0x00, bytes(range(256)) + bytes(43))
r, f, n = try_decode(big)
check("big frame decodes", (r, n, len(big)), (DecodeResult.OK, 309, 309))
check("big frame payload len", len(f.payload), 299)
check("big frame header is 10", len(big) - len(f.payload), 10)
check("big frame length bytes", big[1:3].hex(), "b202")

# The real captured frame header, verbatim from the wire.
real = bytes.fromhex("aab20200001f81002b01") + bytes(299)
r, f, n = try_decode(real)
check("real 0x011F frame decodes", (r, f.cmd_id, f.msg_type, len(f.payload)), (DecodeResult.OK, 0x1F, 0x81, 299))

# Mixed stream: a big frame between small ones must not desync the parser.
mixed = encode_cmd(known.BATTERY_GET, 0x25) + big + encode_cmd(known.EQ_PRESET_GET, 0)
p = FrameParser(); got = p.feed(mixed)
check("parser handles big frame inline", [len(x.payload) for x in got], [0, 299, 0])
check("parser no resync on big frame", p.resync_bytes, 0)
p2 = FrameParser(); got2 = []
for b in mixed: got2.extend(p2.feed(bytes([b])))
check("big frame byte-at-a-time", [x.raw.hex() for x in got2], [x.raw.hex() for x in got])
print()
if fails:
    print(f"{len(fails)} FAILURE(S):")
    for x in fails: print("   ", x)
    sys.exit(1)
print("all checks passed")
