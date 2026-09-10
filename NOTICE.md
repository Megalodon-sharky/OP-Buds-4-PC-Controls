# Notices and attribution

This project reimplements a Bluetooth wire protocol that was reverse-engineered by
others. Protocol facts -- byte layouts, command codes, enum values -- are not
themselves copyrightable, and are used here as documented facts.

**No source code from any of the projects below has been copied into this
repository.** Everything here is written from the specification in
[`docs/protocol.md`](docs/protocol.md).

## Gadgetbridge -- AGPL-3.0

<https://codeberg.org/Freeyourgadget/Gadgetbridge>

Gadgetbridge's Oppo/Realme implementation (`service/devices/oppo/`) was read to
learn the command codes for touch configuration, find-device, misc settings and
subscriptions.

**Gadgetbridge is licensed AGPL-3.0.** Copying its Java source into this project
would attach the AGPL to this project. It is therefore treated strictly as a
research reference and never as a code source. This distinction is deliberate and
must be preserved by anyone contributing here.

## nic0manz -- OnePlus Buds Pro 3 tools

<https://github.com/nic0manz/oneplus_buds3_pro_python>
<https://github.com/nic0manz/oneplus_buds3_pro_dotnet>

Source of the ANC, EQ, custom EQ, BassWave and wear-detection payload layouts, and
of the RFCOMM channel-15 and raw `AF_BTH` socket approach.

Check the upstream licence before reusing any line of that code directly.

## AasheeshLikePanner -- cracked-oneplus-buds

<https://github.com/AasheeshLikePanner/cracked-oneplus-buds>
<https://aasheesh.vercel.app/blog/oneplus-buds>

Source of the OPOv1 framing description, the GATT service and characteristic
UUIDs, and the handshake sequence.

## Trademarks

OnePlus, Oppo, Realme, HeyMelody, BassWave and Golden Sound are trademarks of
their respective owners. This project is not affiliated with, endorsed by, or
supported by any of them.
