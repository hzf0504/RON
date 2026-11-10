$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$zipPath=Join-Path $projectRoot 'releases\RONWG-0.3.0-experimental-win-x64.zip'
$expected=(Get-Content -LiteralPath ($zipPath+'.sha256.txt') -Raw).Split(' ')[0]
if((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expected){throw 'Archive SHA-256 mismatch.'}
Add-Type -AssemblyName System.IO.Compression
$archive=[System.IO.Compression.ZipFile]::OpenRead($zipPath)
try{
    $reader=[System.IO.StreamReader]::new($archive.GetEntry('RONWG/package-manifest.json').Open())
    try{$manifest=$reader.ReadToEnd() | ConvertFrom-Json}finally{$reader.Dispose()}
    foreach($item in $manifest.files){
        $stream=$archive.GetEntry('RONWG/'+$item.file).Open()
        try{if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ne $item.sha256){throw "File hash mismatch: $($item.file)"}}finally{$stream.Dispose()}
    }
    foreach($entry in $archive.Entries){
        if($entry.FullName -notmatch '^RONWG/' -or $entry.FullName -match '(^|/)\.\.(/|$)|:|sessions/|exports/|steam_api|EOSSDK|\.conf$|\.pcap'){throw "Unexpected archive entry: $($entry.FullName)"}
    }
    Write-Output "Verified $($manifest.files.Count) packaged files and archive SHA-256; no personal sessions or game DLLs."
}finally{$archive.Dispose()}
$smoke=Join-Path $projectRoot ('build\package-smoke\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
[System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$smoke)
$exe=Join-Path $smoke 'RONWG\RonWgAssistant.exe'
$versionLog=Join-Path $smoke 'version.txt'
$process=Start-Process -FilePath $exe -ArgumentList '--version' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $versionLog
if($process.ExitCode -ne 0 -or (Get-Content -LiteralPath $versionLog -Raw) -notmatch '0.3.0-experimental'){throw 'Portable version smoke check failed.'}
# All validation above is offline. No game launch, UDP test, or tunnel changes.
Write-Output "Portable executable started successfully. Evidence: $smoke"
