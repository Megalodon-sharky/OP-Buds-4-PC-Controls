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


# --- Feature encodings (all ASSUMED from Buds Pro 3 until measured) --------

ANC_MODE_SET = {"off": 0x01, "anc": 0x02, "transparency": 0x04}

ANC_LEVEL_MASK_SET = {"high": 0x10, "moderate": 0x20, "low": 0x40, "auto": 0x80}

#: Readback masks occupy a different numeric space from the set values, and the
#: Buds Pro 3 and Gadgetbridge already disagree here. Measured in Tier B.
ANC_READBACK_MASK = {
    0x0008: "off",
    0x0100: "transparency",
    0x0010: "high",
    0x0020: "moderate",
    0x0040: "low",
    0x0080: "auto",
}

#: Buds Pro 3 has five presets; the Buds 4 has three (Balanced, Serenade, Bass),
#: so this map is expected to be wrong. Measured in Tier C.
EQ_PRESETS_PRO3 = {0x00: "balanced", 0x01: "bold", 0x02: "serenade", 0x03: "bass", 0x07: "dynaudio"}

EQ_BANDS_HZ = (62, 250, 1000, 4000, 8000, 16000)
EQ_GAIN_MIN = -6
EQ_GAIN_MAX = 6

BASSWAVE_FEATURE_ID = 0x1D
BASSWAVE_MIN = -5
BASSWAVE_MAX = 5

BATTERY_IDS = {0x01: "left", 0x02: "right", 0x03: "case"}

#: Gadgetbridge's misc-config feature ids.
MISC_FEATURES = {"ldac": 0x01, "game_mode": 0x02, "multipoint": 0x03, "find_phone": 0x04}


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
