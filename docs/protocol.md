# OnePlus Buds 4 control protocol

Living document. Every row carries a confidence rating.

| Confidence | Meaning |
|---|---|
| `confirmed` | Observed working against a real OnePlus Buds 4 |
| `likely` | Fits the bytes and survived adversarial review, but rests on one assumption |
| `assumed-pro3` / `assumed-gb` | Inherited from Buds Pro 3 / Gadgetbridge, untested here |
| `unknown` | No information |

---

## Transport

| Fact | Value | Confidence |
|---|---|---|
| Device | OnePlus Buds 4, `12:34:56:78:9A:BC` | `confirmed` |
| Transport | Bluetooth Classic RFCOMM | `confirmed` |
| Control service | `0000079a-d102-11e1-9b23-00025b00a5a5` | `confirmed` |
| How to connect | **By service UUID; let Windows resolve the channel.** | `confirmed` |
| Channel number | Never reported, never needed. Windows returns 0 even on a live link. | `confirmed` |
| Concurrency | Works while the phone is connected. NOT single-session. | `confirmed` |

Other advertised services: `0000111e` (HFP), `df21fe2c-...` (Google Fast Pair Message
Stream), `99999999-...` (unexamined vendor SPP).

**Python trap:** setting a socket timeout puts it in non-blocking mode, so a raw
`ws2_32.connect` returns `WSAEWOULDBLOCK` instead of completing. Drive it by hand:
connect, `select()` for writability, read `SO_ERROR`. See `btsock.connect_uuid`.

---

## Frame format

```
AA <len varint> 00 00 <cmdId> <msgType> <seq> <plenLo> <plenHi> [payload]
```

**`len` is a base-128 varint (LEB128), NOT a single byte.** `confirmed`

```
total  = 1 + len(varint) + len
header = 1 + len(varint) + 7
payload_length == total - header
```

This is the one integrity check available; there is no checksum or CRC anywhere.

The varint is not academic. Command `0x011F` replies with a **299-byte payload** in a
309-byte frame whose length field is `b2 02` (= 306). A single-byte reading predicts 180
bytes and silently mis-frames the stream -- which is exactly how `0x011F` was first
recorded as "never answers", and how 21 notifications were discarded unseen. Audited
across all captures, the varint model parses **85 of 85 received frames with zero
resynchronisations**; the single-byte model does not.

### Message types

| Value | Meaning |
|---|---|
| `0x01` | request |
| `0x81` | reply |
| `0x04` | set |
| `0x84` | set acknowledgement |
| `0x02` | unsolicited notification |
| `0x85` | REGISTER (handshake only) |

**Byte 4 is the command id and byte 5 the message type** -- not one little-endian 16-bit
code, which breaks on REGISTER (`00 85` would read as "command 0x8500"). The `0x0106` /
`0x8106` labels are just `(msgType << 8) | cmdId`. Correlation must key on
`(expectedReplyType, commandId, seq)`: `0x0106` and `0x8106` share id `0x06`, and
notification `0x0204` shares an id with ANC-set `0x0404`.

### payload[0] is usually, but NOT universally, a status byte

In one full sweep, 34 of 41 replies begin `0x00`. The seven exceptions are `0x810E`,
`0x8115`, `0x8116`, `0x8119`, `0x811D`, `0x811E` (begin `0x01`) and `0x812F` (`0x02`).
**In notifications the slot is not a status at all**; see below.

### The error reply has a fixed shape `confirmed`

Measured directly by sending `0x010C` arguments known to be invalid:

```
02 18 0c 20 <varies>
```

`status = 0x02`, then the constant three bytes `18 0c 20`. Byte 2 is `0x0C`, the command
id being complained about. Reproduced across six different malformed `0x010C` requests
(empty payload, and single fields `02`-`06`), always exactly five bytes.

This settles open question 2 -- but the answer is more interesting than expected:

* **`0x02` means the arguments were rejected.** An error is recognisable by shape, not
  just by its first byte.
* **An empty payload is not automatically an error.** `0x010D` with an empty payload
  returns `00 00` -- status OK, *count zero*. It is a list getter being asked for nothing.
* Therefore the six replies beginning `0x01` are **not** error statuses. They do not have
  the error shape. Those commands are still unidentified, but "argument required" has been
  ruled out as the explanation.

---

## Notifications -- THREE families, not one

No published source describes this correctly. An earlier revision of this document had
only the first family.

### Family 1 -- `0x0204`, topic-selected `confirmed`

**payload[0] is a TOPIC selector, and the rest of the body is byte-identical to the
corresponding `0x81xx` reply with its status byte stripped.** One decoder serves both.

| Topic | Body | Same as |
|---|---|---|
| `0x01` | battery list | `0x8106` |
| `0x02` | wear list | `0x8109` |
| `0x03` | ANC state | `0x810C` |
| `0x06` | connected-device table | `0x8112` |
| `0xF1` | `f1 01 01 00 00 03` -- unidentified | -- |
| `0xF2` | one connected device per push, rather than one combined table | -- |
| `0xF4` | **a UTF-8 JSON document** -- see family 3 | -- |

Carries `seq = 0xFF`, except the device-table push which uses an incrementing seq.

### Family 2 -- `0x05xx`, subject in the command id `confirmed`

Here the **command id itself names the subject**, with no topic byte. Critically, it uses
**its own numbering that does not match the getter ids**:

| Push | Subject | Getter id | Payload |
|---|---|---|---|
| `0x0503` | misc config | `0x0D` | `01 1d 00` = `[count][featureId][value]` |
| `0x0504` | EQ preset | `0x0F` | `02` -- bare preset byte |
| `0x0506` | custom EQ | `0x22` | same body as `0x8122` minus status |
| `0x0508` | BassWave | `0x24` | `fb 05 fb` = `[min][max][current]` |

Subject `0x03` is misc config but its getter is `0x0D`; subject `0x04` is EQ preset but its
getter is `0x0F`. **Do not assume the two namespaces align.** Seq is a rolling counter here
(observed `0C 0D 0E 0F 10`), unlike family 1's fixed `0xFF` -- though `0x0504` was also
seen with `0xFF`, so seq is not a reliable family discriminator.

### Family 3 -- topic `0xF4`, JSON telemetry `confirmed`

Topic `0xF4` carries a **UTF-8 JSON document**, keyed by `"cmd"`. Captured verbatim:

```json
{"cmd":"btad","earside":"L","reason":"2"}
{"cmd":"rta","earside":"L","page_timeout":0,"auth_fail":0,"lmp_timeout":0,
 "max_conn":0,"already_exit":0,"limit_resource":0,"other":50,
 "recon_timeout_level":["0","0","0","0"]}
{"earside":"L","cmd":"lhdc","lhdc_start_cnt":64,"lhdc_96k_cnt":0,"lhdc_192k_cnt":0}
{"earside":"L","cmd":"boxchg_err_baseinfo","reg_cnt":1,"general_bp_chg_cnt":0,
 "i2c_exception_len":0,"i2c_exception_cnt":0,...}
```

All diagnostics rather than settings: `btad` = disconnect reason, `rta` = reconnect
analytics, `lhdc` = codec counters, `boxchg_err_baseinfo` = charging-case errors.

**Every message carries `"earside"`.** That is explicit left/right labelling straight from
the device and is the cheapest route to settling the left/right id attribution, which is
otherwise still open.

`lhdc_start_cnt: 64` confirms LHDC is real and in use on this unit -- consistent with LDAC
(`0x01`) being absent from the misc feature list.

### Pushes beat polling

**Battery IS pushed**, contradicting the Gadgetbridge-inherited note that it must be
polled. More importantly, during one run two settings changed *between* poll cycles and
were visible only in the push stream: BassWave's feature flag flipped `00` -> `01` and
back, and feature `0x11` changed immediately before a disconnect. **Treat pushes as the
primary channel and polling as fallback.**

---

## Handshake

| Step | Bytes | Confidence |
|---|---|---|
| HELLO | `AA 07 00 00 00 01 23 00 00 12` | `confirmed` -- replies `0x8100` |
| REGISTER | `AA 0C 00 00 00 85 41 05 00 00 B5 50 A0 69` | **draws no reply**, yet every command works without it |

The trailing `0x12` on HELLO sits outside the frame its length field describes. It is the
only length violation among 322 logged frames and it is transmit-side, so it is a
quirk inherited from the reference implementations, not device behaviour.

**The HELLO reply is NOT a session token.** `00 ff 77 5a ea 67 0e 20 07` is byte-identical
across all five captured sessions. A nonce would vary. It is a static 8-byte value, most
likely a 64-bit feature-support bitmap: bit 29 is set, and BassWave is feature id
`0x1D` (= 29) and is present on this unit. `likely`

---

## The central structure: count-prefixed getters `confirmed`

Most settings-bearing getters share one shape. The request is:

```
[count] [id]...        count ids follow
```

and the reply is:

```
[status] [count] [id][value...] x count
```

**Unsupported ids are silently dropped from the reply rather than erroring.** That makes
this a capability-discovery mechanism: ask for the whole id space in one request and the
hardware answers with exactly the subset it implements.

That is how the tables below were measured rather than guessed, and it is why the whole
settings surface can be re-read in one round trip.

| Command | Record width | Notes |
|---|---|---|
| `0x010D` misc config | 2 = `[id][value]` | 17 features, listed below |
| `0x010C` ANC | 3 = `[id][valueLo][valueHi]` | **only id `0x01` is valid** |
| `0x012F` | 6 = `[id][5 bytes]` | only id `0x01` exists |
| `0x0106` battery | 2 = `[id][raw]` | ids 1/2/3 |
| `0x0109` wear | 2 = `[id][flags]` | ids 1/2/3 |

`0x0108` (touch) looks like one but **is not**: it ignores its request payload entirely.
Payloads `08 01..08`, `01 01` and `01 06` all returned byte-identical 74-byte replies.
Always send it empty.

### Misc-config feature ids -- complete enumeration `confirmed`

Requested ids `0x01`-`0x40`; exactly seventeen came back. Ids `0x21`-`0x40` returned
`00 00`, so **the space ends at `0x20`**.

| id | value | id | value | id | value |
|---|---|---|---|---|---|
| `0x02` | `00` | `0x11` | `01` | `0x18` | `00` |
| `0x04` | `01` | `0x12` | `00` | `0x19` | `00` |
| `0x05` | `01` | `0x14` | `00` | `0x1B` | `00` |
| `0x06` | `00` | `0x16` | `01` | `0x1D` | `01` (BassWave) |
| `0x0B` | `00` | `0x17` | `00` | `0x1E` | `01` |
| `0x0C` | `00` | | | | |
| `0x0D` | `01` | | | | |

Two Gadgetbridge assumptions are now **refuted for this hardware**: feature `0x01` (LDAC)
and `0x03` (multipoint) are absent from the reply, so they are not implemented under those
ids. `0x01`'s absence is consistent with the Buds 4 using LHDC rather than LDAC.

Naming the remaining sixteen is a pure differential exercise -- see `probe watch`. Note
that toggling hi-res, dual connection and 3D Audio on the phone moved **none** of them, so
those three settings do not live here.

### The capability list is DYNAMIC `confirmed`

Mid-session the reply dropped from seventeen features to fifteen: **`0x1D` and `0x1E`
disappeared entirely** -- not set to zero, absent from the reply. Everything else stayed
byte-identical.

`0x1D` is BassWave, and BassWave remained **fully functional throughout**: `0x0124` kept
returning `00 fb 05 05`, and the setting was still usable from the phone.

So an id's absence means **"not currently applicable"**, not "unsupported by this model".
Two explanations were tested and both failed:

* *LHDC gating* -- refuted; hi-res was on and BassWave worked.
* *Active playback gating* -- refuted; audio was paused and the ids did not return.

The only correlate that persists is that a custom EQ profile now exists where none did
before. Unconfirmed. `unknown`

**Design consequence:** any `FeatureSet` must be re-read on state change and on reconnect,
never cached at connect, or the UI will show controls that no longer exist and hide ones
that do. The count-prefixed getter makes the whole list one round trip, so re-reading is
nearly free.

### 3D Audio is not in this protocol `refuted`

Toggling 3D Audio on the phone while polling **all fifty responding command ids** with the
push stream open produced zero changes and no notification. The phone offers only a
"Fixed" mode, with no head-tracked option.

Conclusion: OnePlus 3D Audio is a **phone-side spatial upmix** -- the phone renders
spatialised stereo and transmits ordinary audio over A2DP. There is no bud-side setting.
Do not spend further effort looking for an opcode.

### Custom EQ -- `0x0122` `confirmed`

Fully decoded by creating a profile on the phone while polling. Before creation the reply
was `00 00`; afterwards:

```
[status][profileCount]
  [activeFlag][minGain][maxGain][?][nameLen][name...][bandCount]
  [freqLo][freqHi][gain] x bandCount
```

Observed record: `01 fa 06 04 07 "Custom1" 06` then six bands.

* `minGain` = `0xFA` = **-6**, `maxGain` = `0x06` = **+6**, signed bytes.
* Name is ASCII with an explicit length byte -- `43 75 73 74 6f 6d 31` = `Custom1`.
* The `0x04` between max gain and name length is unidentified; a maximum-profile-count is
  the leading guess. `unknown`
* `activeFlag` went `01` -> `00` the moment the EQ preset switched away from the custom
  profile, so it is selection state, not a profile id. `likely`

**Band frequencies are 16-bit little-endian Hz**, each followed by a signed gain byte.
All six matched the documented band table exactly:

| Bytes | Hz | Bytes | Hz |
|---|---|---|---|
| `3e 00` | 62 | `a0 0f` | 4000 |
| `fa 00` | 250 | `40 1f` | 8000 |
| `e8 03` | 1000 | `80 3e` | 16000 |

Moving the sliders changed only the gain bytes: `00 00 00 00 00 00` ->
`06 06 00 00 fa fa` (+6, +6, 0, 0, -6, -6). Frequencies never moved, confirming the
`[freq16][gain]` split rather than a 24-bit frequency.

### EQ presets -- `0x010F` `confirmed`

Reply is `[status][preset]`. Measured by selecting each preset on the phone:

| Preset | Id |
|---|---|
| Balanced | `0x00` |
| Serenade | `0x01` |
| Bass | `0x02` |
| the saved custom profile | `0x04` |

`0x03` is skipped. The Buds Pro 3 map (`bold` at `0x01`, `serenade` at `0x02`,
`bass` at `0x03`, `dynaudio` at `0x07`) does **not** apply -- as expected, since the
Buds 4 has three presets where the Pro 3 has five.

### BassWave -- `0x0124` `confirmed`

`[status][min][max][current]`, all signed bytes: `00 fb 05 05` = min -5, max +5,
current +5. Established differentially -- moving the slider changed only the last byte
(`05` -> `fb` -> `05`), which ruled out the competing "current first" reading.

### Empty-list getters `confirmed`

These all answer `00 00` -- status OK, count zero -- even when asked for ids `0x01`-`0x20`:

`0x011A` `0x011B` `0x0120` `0x0127` `0x012E` and `0x0122` (custom EQ).

**Proven.** `0x0122` was `00 00`; creating a custom EQ profile on the phone made it
return a full record within one poll cycle, while the other five stayed `00 00`. So
`00 00` means **"a list that is currently empty"**, not "an unimplemented command", and
the list populates the moment the corresponding object exists. `confirmed`

The other five are therefore list getters for objects this unit has never created. A
Golden Sound calibration is the obvious candidate for one of them -- it has never been
run on this unit, and running it is the test. `likely`

---

## Confirmed feature encodings

### Wear / in-ear -- `0x0109`, notification topic `0x02`

`[status][count]` then count x `[id][flags]`. Ids: 1 = left, 2 = right, **3 = case**.

Flag bits, **empirically proven** from live state changes rather than inherited:

| Value | Bits | Meaning |
|---|---|---|
| `0x07` | `111` | present, out of case, in ear |
| `0x05` | `101` | present, out of case, **not** in ear (in hand) |
| `0x04` | `100` | present, in case |
| `0x00` | `000` | not present / link down |

So bit `0x01` = out of case, bit `0x02` = in ear, bit `0x04` = present/valid.

### Battery -- `0x0106`, notification topic `0x01`

`[status][count]` then count x `[id][raw]`, `percent = raw & 0x7F`.
Ids 1 = left, 2 = right, 3 = case.

**id 3 = the charging case is confirmed**: a battery notification gained a third entry
(`03 14` = 20%) at t=326.09, 0.01 s after a wear notification showed the right earbud
entering the case. The case only appears in the list when present.

The `0x80` charging bit is still `assumed-gb` -- no charging sample exists in any capture.

### ANC -- `0x010C` (payload `01 01`), notification topic `0x03`

Request `[count][id...]`, reply `[status][count]` then 3-byte records `[id][maskLo][maskHi]`.

**Only id `0x01` is valid.** Ids `0x02`-`0x06` each return the error shape. This refutes
the inherited Buds Pro 3 claim that field `0x04` reports "the level Auto is applying" --
that field does not exist on this hardware. `confirmed`

Live reading with the buds in transparency mode: `00 01 01 00 01` -> id `0x01`,
mask `0x0100`.

One unexplained result: `02 01 04` (count 2, ids 1 and 4) **succeeds** and returns
`00 02 01 07 08`, even though id `04` alone errors. Either the layout is not
`[count][ids]` after all, or multi-id requests take a different validation path. Five
bytes is too short to hold two 3-byte records, so the reply does not fit the model either.
Flagged rather than guessed -- the Stage 0 phone capture will settle it, and nothing in
the app depends on it. `unknown`

| Mask | Mode | Confidence |
|---|---|---|
| `0x0008` | off | `confirmed` -- set from the phone, read back |
| `0x0010` | noise cancellation | `confirmed` -- set from the phone, read back |
| `0x0100` | transparency | `confirmed` -- set from the phone, read back |
| `0x0800` | **adaptive** | `confirmed` -- set from the phone, read back |
| `0x0020` | moderate | `likely` -- observed as an Auto sub-level |
| `0x0040` | low | `assumed-pro3` |
| `0x0080` | auto | `assumed-pro3` |

Measured by cycling the phone's ANC selector through all four modes while polling
`0x010C`, one mode at a time, ~5 s apart:

```
0x0100 -> 0x0800 -> 0x0010 -> 0x0008
transp.   adaptive  NC        off
```

`0x0010` was previously labelled "high" from the Buds Pro 3. The phone's control is a
plain "Noise Cancellation" entry, so either the Buds 4 exposes no NC sub-levels or one is
selected implicitly. Untested.

### Retracted: the `0x0101` supported-mode mask `refuted`

An earlier revision claimed `0x0101` returns a supported-ANC-mode bitmap (`0x11F8`) and
deduced Adaptive = `0x1000` from `0x11F8 - 0x01F8 = 0x1000` landing with zero bits
missing. **Hardware refuted both.** Adaptive is `0x0800`, and `0x0800` is not a bit in
`0x11F8` at all -- its bits are `0008 0010 0020 0040 0080 0100 1000`.

So whatever `0x0101` returns, the readback masks are not drawn from it. The arithmetic
was clean and the conclusion was still wrong; it survived review because the coincidence
was genuinely tight. Worth remembering when weighing the remaining `likely` rows.

### Connected devices -- `0x0112`, notification topic `0x06`

`[status][count]` then count x `[MAC 6B byte-reversed][attr][0x02][flag][nameLen][name]`.
The record walk closes exactly on 48 bytes with zero residue.

Observed: `5f4e3d2c1b0a` -> `0A:1B:2C:3D:4E:5F` (this PC), name `DESKPC`;
`665544332211` -> `11:22:33:44:55:66` (the phone), name `Owner's OnePlus 13R`.

`DESKPC` is this PC's own hostname -- verified, `$env:COMPUTERNAME` = `DESKPC`. Every
record carries an explicit length byte before its name, so there is no optional-field
problem: a nameless peer would simply carry length `0x00`.

Per-record attr byte: `0x09` for the PC, `0x17` for the phone. The middle `0x02` is
constant in both records. The flag byte is `0x01` for the PC and `0x00` for the phone.

### Touch config -- `0x0108` (send it empty)

`[status][count=0x12]` then **18 records of `[side][type][gesture][action]`**; 2 + 72 = 74
bytes, exact. Side 1 = left, 2 = right. The request payload is **ignored** -- three
different payloads returned byte-identical replies. `confirmed`

Full table as read from this unit:

| type | gesture | left action | right action |
|---|---|---|---|
| `0x01` | `01` | `01` | `01` |
| `0x01` | `02` | `05` | **`06`** |
| `0x01` | `03` | `03` | `03` |
| `0x01` | `04` | `08` | `08` |
| `0x01` | `05` | `07` | `07` |
| `0x01` | `06` | `00` | `00` |
| `0x06` | `02` | `1D` | `1D` |
| `0x06` | `03` | `00` | `00` |
| `0x06` | `06` | `1C` | `1C` |

Left and right differ on exactly one entry: gesture `02` maps to action `05` on the left
and `06` on the right -- consistent with previous/next track on a double tap. `likely`

Type `0x01` carries six gestures, type `0x06` only three (`02`, `03`, `06`) -- consistent
with `0x06` being the in-call context, where fewer gestures are available. `likely`

Type `0x06` entries reference `0x1D` and `0x1C`. `0x1D` is the known BassWave feature id,
so type `0x06` appears to map gestures onto feature ids in the same namespace `0x010D`
queries. `0x1C` is *not* among the seventeen ids `0x010D` reports, which weakens that
reading -- action ids and feature ids may simply be separate namespaces that overlap.

### Other confirmed reads

| Command | Reply | Meaning |
|---|---|---|
| `0x0105` | `00 04` + ASCII `1,2,138,2,2,138,3,1,01,3,2,105` | firmware; two components report build 138, a third reports 105 and 01 |
| `0x010F` | `00 02` | EQ preset, currently 2 |
| `0x0122` | `00 00` | custom EQ, zero saved profiles |
| `0x0124` | `00 fb 05 05` | BassWave `[status][min -5][max +5][current +5]` |
| `0x010D` | `00 01 1d 01` | feature switch: BassWave (`0x1D`) is on |
| `0x012F` | `00 01 01 02 02 00 02 xx` | one 6-byte record, id `01`; the last byte changed between sessions (`00` -> `05`) but was stable across a 5-cycle poll, so it is per-session, not live telemetry |

Live state read while both buds were worn, for cross-checking the decoders:
battery `00 02 01 5a 02 5a` (L 90%, R 90%, case absent);
wear `00 03 01 07 02 07 03 04` (L in ear, R in ear, **case present at `0x04`**).
That is an independent live confirmation of both the wear bit model and id 3 = case,
from a completely separate session to the one that first established them.

Firmware caveat: the 4x3 grouping is the leading reading but is not forced by any length
field, and field 2 may be a major version (making the earbuds `2.138`). Two components
share build 138; a third reports 105 and 01. Left/right attribution is untested.

---

## The 299-byte table -- `0x011F`

Recovered only after the varint fix. Payload structure:

```
00 00 00 00 00 04 44 ac 48 00 01 00 00
then ~24 records of 12 bytes: 08 00 00 00 00 00 00 00 00 00 00 00
```

29 of 299 bytes are non-zero; no ASCII anywhere. `44 ac` reads as `0xAC44` = **44100**
little-endian, an audio sample rate, which puts this in the audio path. The repeating
12-byte record with a single `0x08` and everything else zero looks like a table that
exists but has never been populated.

Leading candidates: a Golden Sound hearing-profile table (the user has never run it), a
multi-band EQ coefficient table, or a codec capability table. Genuinely unresolved --
the differential test in the next section is the way to settle it.

---

## Open questions, ranked by how much they block the app

1. **Name the sixteen unnamed misc-config features.** Pure differential work, no
   guesswork needed: `probe watch` polls all seventeen in one round trip and prints what
   moves when a setting is toggled on the phone. This is the single highest-value
   remaining task and it blocks the Controls and Sound pages.
2. What is `0x011F`'s 299-byte table? Largest unknown structure in the protocol.
3. Why do six replies begin `0x01`? Ruled out as error statuses (they lack the error
   shape), so they are unexplained rather than merely unidentified.
4. What is `0x010C`'s real request layout? `02 01 04` succeeds where `01 04` errors, and
   the 5-byte reply cannot hold two records. Nothing in the app depends on it -- id `0x01`
   alone gives the current mode.
5. Is REGISTER required at all, and is the token wrong?
6. `0x0107` returns `00 02 01 03 02 00` -- ids 1 and 2 with **different** values (3 vs 0)
   while both buds were worn and both at 100%. The only asymmetric field in the corpus, so
   it is the cheapest route to fixing the left/right id mapping.
7. What is topic `0xF1`?
8. What is the `99999999-...` service?

### Settled since the last revision

* Framing is a varint, not a single length byte. `confirmed`
* `0x02` replies are argument-rejection errors with a fixed 5-byte shape. `confirmed`
* The misc-config id space is complete: seventeen features, ending at `0x20`. `confirmed`
* `0x011A` / `0x011B` / `0x0120` / `0x0127` / `0x012E` are empty-list getters, not
  unimplemented commands. `confirmed` (that they are lists), `likely` (what they list)
* ANC has exactly one valid field id. `confirmed`
* `0x0108` ignores its request payload. `confirmed`
* Firmware is an **ASCII** comma-separated string, not a binary structure. `confirmed`
* LDAC (`0x01`) and Gadgetbridge's multipoint id (`0x03`) are absent on this
  hardware. `confirmed`
* ANC map measured for all four modes the phone offers, including adaptive. `confirmed`
* EQ preset ids measured; the Buds Pro 3 map does not apply. `confirmed`
* Custom EQ fully decoded, including band frequencies and signed gains. `confirmed`
* BassWave field order settled differentially. `confirmed`
* There are **three** notification families, not one. `confirmed`
* Topic `0xF4` carries JSON telemetry, with explicit `"earside"` labelling. `confirmed`
* The supported-feature list is dynamic. `confirmed`
* 3D Audio is phone-side and has no bud-side command. `refuted`
* Mid-session disconnect handling works: a real link drop produced a clean message
  and exit code 0, with no traceback. `confirmed`

### Still never attempted

**No SET has ever been transmitted to this hardware.** Everything above is read-only. The
entire write half of the protocol -- ANC set, EQ set, custom EQ upload, touch remapping --
is unverified, and its byte space is separate from the readback space, so none of it can
be inferred from what is confirmed above.
