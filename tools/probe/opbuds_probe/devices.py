"""Paired Bluetooth device enumeration on Windows.

Uses Get-PnpDevice rather than the BTHPORT registry key, because the registry
path needs elevation on some machines while the PnP query does not.
"""

from __future__ import annotations

import json
import re
import subprocess
from dataclasses import dataclass

_PS_QUERY = (
    "Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue | "
    "Where-Object { $_.InstanceId -match 'DEV_[0-9A-Fa-f]{12}' } | "
    "Select-Object Status,FriendlyName,InstanceId | ConvertTo-Json -Compress"
)

# DEV_<mac> sits in the middle of the instance id, e.g.
#   BTHENUM\DEV_123456789ABC\7&2396C45D&1&BLUETOOTHDEVICE_123456789ABC
# so this is deliberately unanchored. "BLUETOOTHDEVICE_" does not contain
# "DEV_" followed by hex, so it cannot produce a false match.
_DEV_RE = re.compile(r"DEV_([0-9A-Fa-f]{12})")

#: Name fragments that identify a device this tool knows how to talk to.
KNOWN_NAME_HINTS = ("buds", "enco", "oneplus", "oppo", "realme")


@dataclass(frozen=True)
class PairedDevice:
    name: str
    mac: str
    status: str

    @property
    def looks_supported(self) -> bool:
        low = self.name.lower()
        return any(h in low for h in KNOWN_NAME_HINTS)

    def __str__(self) -> str:
        flag = "*" if self.looks_supported else " "
        return f"{flag} {self.mac}  {self.status:<12} {self.name}"


def _run_powershell(script: str) -> str:
    result = subprocess.run(
        # powershell.exe is Windows PowerShell 5.1; "powershell" alone may resolve to
        # pwsh 7, which cannot load WinRT types.
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True,
        text=True,
        timeout=30,
    )
    if result.returncode != 0:
        raise RuntimeError(f"powershell failed: {result.stderr.strip()}")
    return result.stdout.strip()


def list_paired() -> list[PairedDevice]:
    """Every paired Bluetooth device whose instance id carries a MAC."""
    raw = _run_powershell(_PS_QUERY)
    if not raw:
        return []
    data = json.loads(raw)
    if isinstance(data, dict):
        data = [data]

    seen: dict[str, PairedDevice] = {}
    for entry in data:
        instance = entry.get("InstanceId") or ""
        match = _DEV_RE.search(instance)
        if not match:
            continue
        hexmac = match.group(1).upper()
        mac = ":".join(hexmac[i : i + 2] for i in range(0, 12, 2))
        name = (entry.get("FriendlyName") or "").strip()
        # Several PnP nodes share one MAC (A2DP, AVRCP, the device itself).
        # Prefer the shortest name, which is the device node rather than a
        # per-profile transport child.
        existing = seen.get(mac)
        if existing is None or len(name) < len(existing.name):
            seen[mac] = PairedDevice(name=name, mac=mac, status=entry.get("Status") or "?")
    return sorted(seen.values(), key=lambda d: (not d.looks_supported, d.name.lower()))


def find_buds(hint: str = "buds") -> PairedDevice | None:
    """First paired device whose name contains ``hint`` (case-insensitive)."""
    hint = hint.lower()
    for dev in list_paired():
        if hint in dev.name.lower():
            return dev
    return None


# NOTE: this uses WinRT, which PowerShell 7 (pwsh) does not project. It must run
# under Windows PowerShell 5.1 -- hence "powershell.exe" rather than "powershell",
# which may be aliased to pwsh. Under pwsh this fails with
# "Unable to find type [Windows.Devices.Bluetooth.BluetoothDevice]".
_PS_SERVICES = """
$ErrorActionPreference = 'Stop'
[Windows.Devices.Bluetooth.BluetoothDevice,Windows.Devices.Bluetooth,ContentType=WindowsRuntime] | Out-Null
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $t) { $asTask.MakeGenericMethod($t).Invoke($null, @($op)).GetAwaiter().GetResult() }
$addr = [uint64]('0xMACHEX')
$dev = Await ([Windows.Devices.Bluetooth.BluetoothDevice]::FromBluetoothAddressAsync($addr)) ([Windows.Devices.Bluetooth.BluetoothDevice])
"STATUS|$($dev.ConnectionStatus)"
$res = Await ($dev.GetRfcommServicesAsync([Windows.Devices.Bluetooth.BluetoothCacheMode]::CACHEMODE)) ([Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceServicesResult])
foreach ($svc in $res.Services) { "SVC|$($svc.ServiceId.Uuid)|$($svc.ConnectionServiceName)" }
"""

#: Windows encodes the resolved channel into ConnectionServiceName as
#: "...#RFCOMM:<port, 8 hex digits>:{uuid}". It reads 00000000 for a cached
#: record on a disconnected device, so a non-zero value here is the cheapest
#: possible channel discovery.
_PORT_RE = re.compile(r"RFCOMM:([0-9A-Fa-f]{1,8}):")


@dataclass(frozen=True)
class RfcommService:
    uuid: str
    channel: int | None
    service_name: str


@dataclass(frozen=True)
class SdpResult:
    connected: bool
    services: list[RfcommService]

    def find(self, uuid: str) -> RfcommService | None:
        uuid = uuid.lower()
        return next((s for s in self.services if s.uuid == uuid), None)


def query_rfcomm_services(mac: str, uncached: bool = False) -> SdpResult:
    """Ask Windows what RFCOMM services the device advertises.

    An uncached query needs the device to be connected; on a disconnected device
    it returns nothing at all. The cached record survives disconnection, which is
    why the default here is cached.
    """
    hexmac = mac.replace(":", "").replace("-", "").upper()
    mode = "Uncached" if uncached else "Cached"
    script = _PS_SERVICES.replace("MACHEX", hexmac).replace("CACHEMODE", mode)
    try:
        raw = _run_powershell(script)
    except (RuntimeError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError(f"SDP query failed for {mac}: {exc}") from exc

    connected = False
    services: list[RfcommService] = []
    for line in raw.splitlines():
        line = line.strip()
        if line.startswith("STATUS|"):
            connected = line.split("|", 1)[1].strip().lower() == "connected"
        elif line.startswith("SVC|"):
            _, uuid, name = (line.split("|", 2) + ["", ""])[:3]
            match = _PORT_RE.search(name)
            port = int(match.group(1), 16) if match else None
            services.append(
                RfcommService(uuid=uuid.strip().lower(), channel=port or None, service_name=name.strip())
            )
    return SdpResult(connected=connected, services=services)