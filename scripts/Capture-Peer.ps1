# Requires an otherwise idle pktmon session. Does not clear anyone else's filters.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Peer,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(10,600)][int]$Seconds=60
)
$ErrorActionPreference='Stop'
$ip=[System.Net.IPAddress]::Parse($Peer)
if ($ip.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or [System.Net.IPAddress]::IsLoopback($ip) -or $ip.GetAddressBytes()[0] -ge 224 -or $ip.GetAddressBytes()[0] -eq 0) { throw 'A peer unicast IPv4 is required.' }
$outputPath=[System.IO.Path]::GetFullPath($OutputDirectory)
$packageRoot=[System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $outputPath.StartsWith($packageRoot+[System.IO.Path]::DirectorySeparatorChar,[System.StringComparison]::OrdinalIgnoreCase)) { throw 'Write capture files under this workspace/package only.' }
$sessions=& logman query -ets 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect ETW sessions.' }
if ($sessions -match 'PktMon') { throw 'An existing PktMon trace is running; leave it untouched and finish it first.' }
$filters=& pktmon filter list 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect PktMon filters.' }
if ($filters -match '^\s*\d+\s+') { throw 'Existing PktMon filters detected; this script will not alter or broaden them.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$name='RonWg-'+[guid]::NewGuid().ToString('N')
$etl=Join-Path $outputPath ($name+'.etl')
$started=$false
try {
    & pktmon list --json | Set-Content -LiteralPath (Join-Path $outputPath ($name+'-components.json')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list packet monitor components.' }
    & pktmon filter add $name -i $Peer -t UDP
    if ($LASTEXITCODE -ne 0) { throw 'Cannot install peer-only packet filter.' }
    & pktmon start --capture --comp nics --pkt-size 64 --file-size 64 --log-mode circular --file-name $etl
    if ($LASTEXITCODE -ne 0) { throw 'Capture did not start.' }
    $started=$true
    Write-Output "Capturing peer UDP for $Seconds seconds. Keep helper UDP probes stopped. The ETL retains NIC/component metadata."
    for($i=0;$i -lt $Seconds;$i++) { Start-Sleep -Seconds 1 }
} finally {
    if($started) { & pktmon stop }
    & pktmon filter remove $name
}
if($started) {
    & pktmon etl2txt $etl --out (Join-Path $outputPath ($name+'.txt'))
    if ($LASTEXITCODE -ne 0) { throw 'ETL conversion failed; original ETL was retained.' }
    & pktmon etl2pcap $etl --out (Join-Path $outputPath ($name+'.pcapng'))
    Write-Output 'Capture is supporting evidence only. Match timestamps, 5-tuples and WG components to native.jsonl; packets alone do not prove gameplay.'
}
