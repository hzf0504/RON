# Optional, narrowly scoped rules. This script is NOT run by the ordinary UI.
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('Add','Remove')][string]$Action,
    [string]$GameRoot,
    [int]$InterfaceIndex,
    [string[]]$Peer,
    [switch]$IncludeGame
)
$ErrorActionPreference='Stop'
$packageRoot=Split-Path -Parent $PSScriptRoot
$assistantPath=Join-Path $packageRoot 'RonWgAssistant.exe'
if (-not (Test-Path -LiteralPath $assistantPath)) { $assistantPath=Join-Path $packageRoot 'dist\RonWgAssistant\RonWgAssistant.exe' }
$stateDir=Join-Path $packageRoot 'local-state'
$statePath=Join-Path $stateDir 'firewall.json'
if ($Action -eq 'Remove') {
    if (-not (Test-Path -LiteralPath $statePath)) { Write-Output 'No owned firewall rules recorded.'; return }
    $owned=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    foreach ($rule in $owned) {
        if ($rule.Name -notmatch '^RonWgAssistant-[0-9a-f]{32}$') { throw 'Invalid ownership record.' }
        $live=Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
        if ($live) {
            $app=$live | Get-NetFirewallApplicationFilter
            if ($app.Program -ne $rule.Program) { throw 'Rule changed externally; inspect it manually.' }
            $live | Remove-NetFirewallRule
        }
    }
    Remove-Item -LiteralPath $statePath
    Write-Output 'Removed only rules recorded as owned by this assistant.'; return
}
if ($Action -ne 'Add') { throw 'Specify -Action Add or Remove.' }
if (Test-Path -LiteralPath $statePath) { throw 'An owned rule set exists. Use -Action Remove before changing it.' }
if (-not (Test-Path -LiteralPath $assistantPath)) { throw 'Assistant executable not found.' }
$adapter=Get-NetAdapter -InterfaceIndex $InterfaceIndex -ErrorAction Stop
if ($adapter.Status -ne 'Up' -or $adapter.InterfaceDescription -notmatch 'WireGuard') { throw 'Choose an active WireGuard adapter.' }
$local=@(Get-NetIPAddress -InterfaceIndex $InterfaceIndex -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
if ($local.Count -eq 0 -or $Peer.Count -lt 1 -or $Peer.Count -gt 4) { throw 'IPv4 address and 1-4 peer addresses are required.' }
foreach($address in $Peer) {
    $ip=[System.Net.IPAddress]::Parse($address)
    if ($ip.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or [System.Net.IPAddress]::IsLoopback($ip) -or $ip.GetAddressBytes()[0] -ge 224 -or $ip.GetAddressBytes()[0] -eq 0 -or $local -contains $address) { throw 'Peer must be a unicast IPv4 address of a friend.' }
    $route=Find-NetRoute -RemoteIPAddress $address | Where-Object { $_.PSObject.Properties.Name -contains 'DestinationPrefix' }
    if ($route.InterfaceIndex -ne $InterfaceIndex) { throw "Peer route does not use selected WG: $address" }
}
$programs=@([pscustomobject]@{Program=(Resolve-Path -LiteralPath $assistantPath).Path;Port='39741'})
if($IncludeGame) {
    $gamePath=Join-Path $GameRoot 'ReadyOrNot\Binaries\Win64\ReadyOrNotSteam-Win64-Shipping.exe'
    $programs += [pscustomobject]@{Program=(Resolve-Path -LiteralPath $gamePath).Path;Port='Any'}
}
New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
$created=@()
try {
    foreach($program in $programs) {
        $name='RonWgAssistant-'+[guid]::NewGuid().ToString('N')
        # Write intent first so an interrupted invocation remains recoverable.
        $created += [pscustomobject]@{Name=$name;Program=$program.Program}
        ConvertTo-Json -InputObject @($created) | Set-Content -LiteralPath $statePath -Encoding utf8
        New-NetFirewallRule -Name $name -DisplayName 'RON WG assistant (specific peers only)' -Group 'RON WG Assistant' -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol UDP -Program $program.Program -LocalPort $program.Port -LocalAddress $local -RemoteAddress $Peer -InterfaceAlias $adapter.Name | Out-Null
    }
} catch {
    foreach($rule in $created) { Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
    throw
}
Write-Output "Created $($created.Count) scoped rule(s). Restore with this script -Action Remove."
