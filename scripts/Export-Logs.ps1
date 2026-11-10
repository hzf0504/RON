param([Parameter(Mandatory=$true)][string]$SessionDirectory)
$ErrorActionPreference='Stop'
$packageRoot=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $packageRoot 'RonWgAssistant.exe'
if(-not (Test-Path -LiteralPath $exe)){$exe=Join-Path $packageRoot 'dist\RonWgAssistant\RonWgAssistant.exe'}
$source=(Resolve-Path -LiteralPath $SessionDirectory).Path
$exportDir=Join-Path (Split-Path -Parent $exe) 'exports'
& $exe --export $source $exportDir | Out-Host
if($LASTEXITCODE -ne 0){throw 'Log export failed. Original logs were kept.'}
Get-ChildItem -LiteralPath $exportDir -Filter '*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 FullName,Length
