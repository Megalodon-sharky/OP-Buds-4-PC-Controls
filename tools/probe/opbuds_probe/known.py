"""Known command codes, enums and per-model constants.

Sources, in descending order of confidence for the Buds 4:

* Gadgetbridge's Oppo/Realme implementation (production code, several models) --
  command codes for battery, firmware, ANC, touch, misc config, find-device,
  subscriptions.
* nic0manz's OnePlus Buds Pro 3 tools -- EQ, custom EQ, BassWave, wear, and the
  payload layouts for those.
* The Nord Buds 3 Pro blog post -- the handshake and framing, over BLE.

None of it was tested against a Buds 4. Everything here marked ASSUMED is a
starting hypothesis for the probe to confirm or refute, not a fact.
"""

from __future__ import annotations

# --- Commands -------------------------------------------------------------
# Labels are the conventional (msg_type << 8) | cmd_id form.

# Handshake
HELLO = 0x0100
REGISTER = 0x8500

# Requests (msg_type 0x01) -> replies (0x81)
FIRMWARE_GET = 0x0105
BATTERY_GET = 0x0106
TOUCH_CONFIG_GET = 0x0108
WEAR_GET = 0x0109
ANC_GET = 0x010C
MISC_CONFIG_GET = 0x010D
EQ_PRESET_GET = 0x010F
CUSTOM_EQ_GET = 0x0122
BASSWAVE_VALUE_GET = 0x0124

# Sets (msg_type 0x04) -> acks (0x84)
FIND_DEVICE_SET = 0x0400
TOUCH_CONFIG_SET = 0x0401
MISC_CONFIG_SET = 0x0403
ANC_SET = 0x0404
EQ_PRESET_SET = 0x0406
CUSTOM_EQ_SET = 0x0418
BASSWAVE_LEVEL_SET = 0x041B

# Subscriptions / notifications
SUBSCRIPTION_SET = 0x0205
SUBSCRIPTION_NOTIFY = 0x0204

COMMAND_NAMES = {
    HELLO: "HELLO",
    REGISTER: "REGISTER",
    FIRMWARE_GET: "FIRMWARE_GET",
    BATTERY_GET: "BATTERY_GET",
    TOUCH_CONFIG_GET: "TOUCH_CONFIG_GET",
    WEAR_GET: "WEAR_GET",
    ANC_GET: "ANC_GET",
    MISC_CONFIG_GET: "MISC_CONFIG_GET",
    EQ_PRESET_GET: "EQ_PRESET_GET",
    CUSTOM_EQ_GET: "CUSTOM_EQ_GET",
    BASSWAVE_VALUE_GET: "BASSWAVE_VALUE_GET",
    FIND_DEVICE_SET: "FIND_DEVICE_SET",
    TOUCH_CONFIG_SET: "TOUCH_CONFIG_SET",
    MISC_CONFIG_SET: "MISC_CONFIG_SET",
    ANC_SET: "ANC_SET",
    EQ_PRESET_SET: "EQ_PRESET_SET",
    CUSTOM_EQ_SET: "CUSTOM_EQ_SET",
    BASSWAVE_LEVEL_SET: "BASSWAVE_LEVEL_SET",
    SUBSCRIPTION_SET: "SUBSCRIPTION_SET",
    SUBSCRIPTION_NOTIFY: "SUBSCRIPTION_NOTIFY",
}


def command_name(command: int) -> str:
    """Human name for a 16-bit command label, including reply forms."""
    if command in COMMAND_NAMES:
        return COMMAND_NAMES[command]
    cmd_id = command & 0xFF
    msg_type = (command >> 8) & 0xFF
    # A reply carries the request's id with the type flipped to 0x81/0x84.
    for base_type, reply_type, suffix in ((0x01, 0x81, " reply"), (0x04, 0x84, " ack")):
        if msg_type == reply_type:
            base = (base_type << 8) | cmd_id
            if base in COMMAND_NAMES:
                return COMMAND_NAMES[base] + suffix
    if msg_type in (0x02, 0x05):
        return f"notification (id 0x{cmd_id:02X})"
    return "unknown"


# --- Sequence numbers -----------------------------------------------------
# nic0manz hardcodes a per-command seq; Gadgetbridge uses a rolling counter.
# Which the Buds 4 tolerate is unknown, so both are selectable.

FIXED_SEQ = {
    HELLO: 0x23,
    REGISTER: 0x41,
    BATTERY_GET: 0x25,
    ANC_SET: 0x42,
}
DEFAULT_SEQ = 0x00


def fixed_seq_for(command: int) -> int:
    return FIXED_SEQ.get(command, DEFAULT_SEQ)


# --- Handshake ------------------------------------------------------------
# HELLO's length byte covers bytes 2..8 only, so the trailing 0x12 in the
# "classic" variant sits outside the frame. Unexplained, but byte-identical
# across every published implementation, so it is probably meaningful.

HELLO_CLASSIC = bytes.fromhex("aa070000000123000012")
HELLO_NO_TRAILER = bytes.fromhex("aa07000000012300 00".replace(" ", ""))
HELLO_LEN_FIXED = bytes.fromhex("aa080000000123010012")

HELLO_VARIANTS = {
    "classic": HELLO_CLASSIC,
    "no-trailer": HELLO_NO_TRAILER,
    "len-fixed": HELLO_LEN_FIXED,
}

#: Device token in the REGISTER payload. Documented as possibly device-specific.
REGISTER_TOKEN_DEFAULT = bytes.fromhex("b550a069")
REGISTER_TOKEN_ZERO = bytes(4)
REGISTER_TOKENS = {
    "default": REGISTER_TOKEN_DEFAULT,
    "zero": REGISTER_TOKEN_ZERO,
}

#: Full REGISTER payload is a leading 0x00 then the token.
def register_payload(token: bytes = REGISTER_TOKEN_DEFAULT) -> bytes:
    return b"\x00" + token


# --- Transport ------------------------------------------------------------

OPO_SERVICE_UUID = "0000079a-d102-11e1-9b23-00025b00a5a5"

#: Confirmed present in the Buds 4 SDP record alongside the OPO service.
FASTPAIR_MESSAGE_STREAM_UUID = "df21fe2c-2515-4fdb-8886-f12c4d67927c"
UNKNOWN_VENDOR_UUID = "99999999-9999-9999-9999-999999999999"

#: nic0manz hardcodes this for the Buds Pro 3. ASSUMED for the Buds 4.
LEGACY_RFCOMM_CHANNEL = 15

#: Channels used by standard audio profiles. The brute scan skips these so a
#: probe run cannot interrupt playback or a call.
AUDIO_PROFILE_CHANNELS = {1, 2, 3}


# --- Read-only sweep ------------------------------------------------------
# Every entry is msg_type 0x01, which cannot change device state.

GET_SWEEP = [
    (FIRMWARE_GET, b""),
    (BATTERY_GET, b""),
    (TOUCH_CONFIG_GET, b""),
    (WEAR_GET, b""),
    (ANC_GET, bytes((0x01, 0x01))),   # field 01 = current mode
    (ANC_GET, bytes((0x04, 0x01))),   # field 04 = level Auto is applying
    (MISC_CONFIG_GET, bytes((0x01, 0x1D))),  # count=1, BassWave feature id
    (EQ_PRESET_GET, b""),
    (CUSTOM_EQ_GET, b""),
    (BASSWAVE_VALUE_GET, b""),
]


# --- SET allowlist --------------------------------------------------------
# Nothing outside this set is ever transmitted with msg_type 0x04. An unknown
# setter in the 0x04xx space could be a factory reset or a DFU entry point, so
# the space is never fuzzed.

SET_ALLOWLIST = {
    FIND_DEVICE_SET,
    ANC_SET,
    EQ_PRESET_SET,
    CUSTOM_EQ_SET,
    TOUCH_CONFIG_SET,
    MISC_CONFIG_SET,
    BASSWAVE_LEVEL_SET,
}

SET_TIERS = {
    "A": {FIND_DEVICE_SET},
    "B": {ANC_SET},
    "C": {EQ_PRESET_SET},
    "D": {CUSTOM_EQ_SET, TOUCH_CONFIG_SET, MISC_CONFIG_SET, BASSWAVE_LEVEL_SET},
}


# --- Feature encodings ----------------------------------------------------
# Readback values below were MEASURED on a real Buds 4 by changing each setting
# from the phone and reading the result back. The SET values below are no longer
# inherited guesses: Tier A/B/C wrote to this hardware on 2026-09-10 and every
# value marked CONFIRMED was measured by writing it and reading the result back.
# Set and readback still occupy different numeric spaces -- see anc_set_payload.

#: CONFIRMED by writing every byte 0x01..0x20 and reading the mode back.
#: The field is a bitmask with a priority order, not an enum:
#:   bit 0x01 (off) wins over everything -- every odd value read back as off
#:   bit 0x04 (transparency) beats bit 0x02 (anc)
#: The upper nibble is the LEVEL, and it maps straight through to the readback
#: mask: set 0x10 -> mask 0x0010, set 0x20 -> mask 0x0020.
ANC_MODE_SET = {"off": 0x01, "anc": 0x02, "transparency": 0x04}

#: CONFIRMED for high, moderate and auto by writing the byte and reading back;
#: low was confirmed from the readback side when chosen on the phone. Combine
#: with a mode bit, e.g. anc+high = 0x02 | 0x10.
ANC_LEVEL_MASK_SET = {"high": 0x10, "moderate": 0x20, "low": 0x40, "auto": 0x80}

#: CONFIRMED on a Buds 4, except where noted. Established both by cycling the
#: phone's ANC selector and by writing set bytes and reading the result back.
ANC_READBACK_MASK = {
    0x0008: "off",           # confirmed both ways
    0x0010: "anc_high",      # confirmed: the phone's "Noise Cancellation"
                             # selects ANC at high level; set 0x10 reaches it
    0x0020: "anc_moderate",  # confirmed: reached by writing set byte 0x20
    0x0040: "anc_low",       # confirmed: predicted from the level pattern, then
                             # observed when the level was chosen on the phone
    0x0080: "anc_auto",      # confirmed by writing set byte 0x80, and present in
                             # the phone's own level list. Distinct from adaptive
    0x0100: "transparency",  # confirmed both ways
    0x0800: "adaptive",      # confirmed: needs the 16-bit set form 01 01 00 08
}

#: CONFIRMED: the ANC set VALUE IS 16-BIT LITTLE-ENDIAN and mirrors the readback
#: mask. Payload is [count][id][valueLo][valueHi]. Adaptive was unreachable for
#: an entire session only because every write had been 3 bytes, which cannot
#: express a value living in the high byte.
#:
#: The field carries two encodings at once, which is why a byte sweep looks
#: irregular. Low-byte values 0x10/0x20/0x40/0x80 pass straight through to mask
#: bits 0x0010..0x0080. But 0x01/0x02/0x04 are NOT mask bits -- they are
#: convenience mode selectors the firmware expands:
#:     0x01 -> 0x0008 (off)   0x02 -> 0x0010 (anc, defaults to high)
#:     0x04 -> 0x0100 (transparency)
#: Prefer the explicit 16-bit form everywhere; it is unambiguous.
def anc_set_payload(mask: int, count: int = 0x01, field: int = 0x01) -> bytes:
    """Build an ANC_SET payload for a readback mask, e.g. ANC_MASK_ADAPTIVE."""
    if mask not in ANC_READBACK_MASK:
        raise ValueError(f"not a known ANC mask: 0x{mask:04X}")
    return bytes((count, field, mask & 0xFF, (mask >> 8) & 0xFF))


ANC_MASK_OFF = 0x0008
ANC_MASK_HIGH = 0x0010
ANC_MASK_MODERATE = 0x0020
ANC_MASK_LOW = 0x0040
ANC_MASK_AUTO = 0x0080
ANC_MASK_TRANSPARENCY = 0x0100
ANC_MASK_ADAPTIVE = 0x0800

#: All seven confirmed by writing the 16-bit form and reading the mode back.
#: "auto" (0x0080) IS present in the phone's own level list -- an earlier
#: revision claimed otherwise, having missed it in a ~5 s watch poll cycle.
#: Absence from a poll is not absence from the device.
ANC_STATES_CONFIRMED = (ANC_MASK_OFF, ANC_MASK_HIGH, ANC_MASK_MODERATE,
                        ANC_MASK_LOW, ANC_MASK_AUTO, ANC_MASK_TRANSPARENCY,
                        ANC_MASK_ADAPTIVE)

#: CONFIRMED. Measured by selecting each preset on the phone and reading 0x010F.
#: 0x04 selects the saved custom profile.
#:
#: RETRACTION: an earlier revision said "0x03 is skipped by the hardware".
#: That was inferred from the phone never emitting 0x03, and it is wrong.
#: Tier C wrote 0x00-0x07 to EQ_PRESET_SET and the firmware accepted every
#: single one: set-ack status 0x00, a genuine 0x0504 state-change
#: notification, and a readback mirroring the value exactly. The setter
#: performs NO range validation. See EQ_PRESETS_UNVALIDATED below.
EQ_PRESETS_BUDS4 = {0x00: "balanced", 0x01: "serenade", 0x02: "bass", 0x04: "custom"}

#: CONFIRMED (Tier C, 2026-09-10): EQ_PRESET_SET stores and echoes any byte in
#: 0x00-0x07 without complaint. Whether 0x03/0x05/0x06/0x07 map to a real DSP
#: curve is UNKNOWN -- storage and notification were proved, audible effect was
#: not. Callers MUST validate against EQ_PRESETS_BUDS4 before transmitting; the
#: device will not reject a bad value and the phone UI may then show no
#: selection at all.
EQ_PRESETS_UNVALIDATED = True
EQ_PRESET_ACCEPTED_RANGE = range(0x00, 0x08)

#: Kept for the Buds Pro 3 regression profile. Does NOT apply to the Buds 4.
EQ_PRESETS_PRO3 = {0x00: "balanced", 0x01: "bold", 0x02: "serenade", 0x03: "bass", 0x07: "dynaudio"}

#: CONFIRMED: every band frequency below appeared verbatim in a custom EQ reply
#: as a 16-bit little-endian value, each followed by a signed gain byte.
EQ_BANDS_HZ = (62, 250, 1000, 4000, 8000, 16000)
EQ_GAIN_MIN = -6
EQ_GAIN_MAX = 6

BASSWAVE_FEATURE_ID = 0x1D
BASSWAVE_MIN = -5
BASSWAVE_MAX = 5

BATTERY_IDS = {0x01: "left", 0x02: "right", 0x03: "case"}

#: CONFIRMED: ids 0x01-0x40 were requested from 0x010D and exactly these
#: seventeen came back; 0x21-0x40 returned nothing, so the space ends at 0x20.
#: Names are NOT known -- toggling hi-res, dual connection and 3D Audio on the
#: phone moved none of them. Do not guess; measure with `probe watch`.
#:
#: Gadgetbridge's ids are REFUTED for this hardware: ldac (0x01) and multipoint
#: (0x03) are both absent from the reply. The absence of LDAC is consistent with
#: the Buds 4 using LHDC, which the JSON telemetry channel confirms is active.
MISC_IDS_MEASURED = (0x02, 0x04, 0x05, 0x06, 0x0B, 0x0C, 0x0D, 0x11, 0x12,
                     0x14, 0x16, 0x17, 0x18, 0x19, 0x1B, 0x1D, 0x1E)

#: Gadgetbridge's misc-config feature ids. Refuted above; kept for reference.
MISC_FEATURES_GADGETBRIDGE = {"ldac": 0x01, "game_mode": 0x02,
                              "multipoint": 0x03, "find_phone": 0x04}

#: REFUTED 2026-09-10. An earlier revision here claimed the supported-feature
#: list was DYNAMIC, because 0x1D and 0x1E "vanished" mid-session while BassWave
#: stayed fully functional. They never vanished. The 0x010D reply is capped at
#: 32 payload bytes -- exactly 15 [id][value] pairs -- and entries come back in
#: ascending id order, so a request listing more than 15 supported ids silently
#: loses the highest ones off the end.
#:
#: The reply is not merely truncated, it is MISLEADING: the count field reports
#: the truncated count as though it were the real one, so a caller that trusts
#: it sees a smaller, entirely plausible feature list with no error anywhere.
#: Requesting only (0x1D, 0x1E) returns `00 02 1d 01 1e 01` -- both supported,
#: both enabled, exactly as they always were.
#:
#: The list IS static. Page requests at MISC_MAX_IDS_PER_REPLY and it is safe to
#: cache for the lifetime of a connection.
MISC_LIST_IS_DYNAMIC = False

#: CONFIRMED: a 16-id request returned 15 entries in a payload of exactly 0x20
#: bytes. Never send more ids than this in one 0x010D request.
MISC_MAX_IDS_PER_REPLY = 15
MISC_REPLY_PAYLOAD_CAP = 32


def misc_id_pages(ids=MISC_IDS_MEASURED, per_page: int = MISC_MAX_IDS_PER_REPLY):
    """Split misc ids into requests small enough to survive the 32-byte cap."""
    ids = tuple(ids)
    return [ids[i:i + per_page] for i in range(0, len(ids), per_page)]


# --- Notifications ---------------------------------------------------------
# Three distinct families, which is not obvious from any published source.

#: Family 1: 0x0204, seq 0xFF. payload[0] selects a topic; the remainder is
#: byte-identical to the matching 0x81xx reply minus its status byte.
NOTIFY_TOPICS = {
    0x01: "battery",            # confirmed, same body as 0x8106
    0x02: "wear",               # confirmed, same body as 0x8109
    0x03: "anc",                # confirmed, same body as 0x810C
    0x06: "connected_devices",  # confirmed, same body as 0x8112
    0xF1: "unidentified",
    0xF2: "connected_device_single",  # one record per peer
    0xF4: "json_telemetry",     # confirmed: a JSON document, see below
}

#: Family 2: 0x05xx, where the COMMAND ID itself names the subject. This uses a
#: numbering of its own that does not match the getter ids -- misc config is
#: subject 0x03 but getter 0x0D, EQ preset subject 0x04 but getter 0x0F.
NOTIFY_SUBJECTS = {
    0x03: "misc_config",   # [count][id][value], confirmed
    0x04: "eq_preset",     # bare preset byte, confirmed
    0x06: "custom_eq",     # same body as 0x8122 minus status, confirmed
    0x08: "basswave",      # [min][max][current], confirmed
}

#: Family 3: topic 0xF4 carries a UTF-8 JSON document, keyed by "cmd". Observed
#: types, all diagnostics rather than settings. Every one carries "earside",
#: which is the cheapest available route to fixing left/right attribution.
JSON_TELEMETRY_COMMANDS = ("btad", "rta", "lhdc", "boxchg_err_baseinfo")


def decode_battery(payload: bytes) -> list[dict]:
    """Decode a battery reply payload: [status][count] then count x [id][raw].

    The high bit of each raw byte is a charging flag; the low seven bits are the
    percentage.
    """
    if len(payload) < 2:
        return []
    count = payload[1]
    out = []
    for i in range(count):
        off = 2 + i * 2
        if off + 1 >= len(payload):
            break
        bid, raw = payload[off], payload[off + 1]
        out.append(
            {
                "id": bid,
                "name": BATTERY_IDS.get(bid, f"unknown-0x{bid:02X}"),
                "percent": raw & 0x7F,
                "charging": bool(raw & 0x80),
            }
        )
    return out
