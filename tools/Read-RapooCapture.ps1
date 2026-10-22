#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CapturePath,
    [ValidateRange(1, 127)][int]$DeviceAddress = 10,
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe'
)

# Offline USBPcap analysis only. Never opens a device or sends a report.
$ErrorActionPreference = 'Stop'
$resolvedCapture = (Resolve-Path -LiteralPath $CapturePath).Path
$json = & $TsharkPath -r $resolvedCapture -Y "usb.device_address == $DeviceAddress && usb.transfer_type == 0x02" -T json -x -j 'frame usb'
if ($LASTEXITCODE -ne 0) { throw "tshark failed with exit code $LASTEXITCODE" }
$packets = ($json -join "`n") | ConvertFrom-Json -AsHashtable

foreach ($packet in $packets) {
    $layers = $packet['_source']['layers']
    # USBPcap pseudoheader; reject other link types rather than guessing offsets.
    if ($layers['frame']['frame.encap_type'] -ne '152') {
        throw 'Expected USBPcap encapsulation (Wireshark type 152).'
    }
    $raw = [Convert]::FromHexString($layers['frame_raw'][0])
    if ($raw.Length -lt 28) { continue }
    $headerLength = [int]$raw[0] -bor ([int]$raw[1] -shl 8)
    if ($headerLength -lt 28 -or $headerLength -ge $raw.Length -or $raw[22] -ne 2) { continue }
    $payload = $raw[$headerLength..($raw.Length - 1)]
    $kind = $null
    $register = $null
    $data = $null
    $length = $null
    if ($raw[27] -eq 0 -and $raw.Length -ge ($headerLength + 8)) {
        # HID SET_REPORT(Output BA), Interface 1. Exclude unrelated control data.
        if ($payload[0] -ne 0x21 -or $payload[1] -ne 9 -or
            $payload[2] -ne 0xBA -or $payload[3] -ne 2 -or
            $payload[4] -ne 1 -or $payload[5] -ne 0) { continue }
        if ($payload.Length -lt 16) { continue }
        $report = $payload[8..($payload.Length - 1)]
        if ($report[0] -ne 0xBA -or $report[1] -ne 0xA5) { continue }
        $kind = switch ($report[2]) { 0xA4 { 'Read' } 0xA5 { 'Write' } 0xA3 { 'Commit' } default { 'Unknown' } }
        $length = [int]$report[3]
        $register = '{0:X2}{1:X2}' -f $report[5], $report[4]
        if ($kind -eq 'Write') {
            if ($length -gt ($report.Length - 8)) { throw "Truncated report in frame $($layers['frame']['frame.number'])" }
            $data = if ($length -gt 0) { [Convert]::ToHexString([byte[]]$report[8..(7 + $length)]) } else { '' }
        }
        $hex = [Convert]::ToHexString([byte[]]$report)
    } elseif ($raw[27] -eq 3 -and $raw[21] -eq 0x80 -and $raw.Length -gt $headerLength) {
        $kind = 'Response'
        $hex = [Convert]::ToHexString([byte[]]$payload)
    } else { continue }
    [pscustomobject]@{
        Frame = [int]$layers['frame']['frame.number']
        Time = $layers['frame']['frame.time_relative']
        Bus = [int]$raw[17] -bor ([int]$raw[18] -shl 8)
        Device = $DeviceAddress
        Kind = $kind
        Register = $register
        Length = $length
        Data = $data
        Hex = $hex
    }
}
