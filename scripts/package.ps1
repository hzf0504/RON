param([string]$Version='0.3.0-experimental')
$ErrorActionPreference='Stop'
if($Version -notmatch '^[A-Za-z0-9.-]+$'){throw 'Invalid package version.'}
$projectRoot=Split-Path -Parent $PSScriptRoot
$dist=Join-Path $projectRoot 'dist\RonWgAssistant'
$release=Join-Path $projectRoot 'releases'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$zipPath=Join-Path $release "RONWG-$Version-win-x64.zip"
# Explicit allowlist. Do NOT zip the build tree, sessions, exports, or game folder.
$build=Get-Content -LiteralPath (Join-Path $dist 'build-info.json') -Raw | ConvertFrom-Json
if(-not $build.selfContained -or $build.version -ne $Version){throw 'Build a matching self-contained release first.'}
if($build.exeSha256 -ne (Get-FileHash -LiteralPath (Join-Path $dist 'RonWgAssistant.exe')).Hash -or $build.probeSha256 -ne (Get-FileHash -LiteralPath (Join-Path $dist 'RonWgProbe.dll')).Hash){throw 'Build outputs changed; rebuild before packaging.'}
if($build.latencySha256 -ne (Get-FileHash -LiteralPath (Join-Path $dist 'RonLatency.dll')).Hash){throw 'Latency output changed; rebuild before packaging.'}
$files=@('RonWgAssistant.exe','RonWgProbe.dll','RonLatency.dll','compatibility.json','build-info.json','README.md','TESTER_GUIDE.md','LOG_FORMAT.md',
    'scripts\Firewall.ps1','scripts\Capture-Peer.ps1','scripts\Export-Logs.ps1',
    'licenses\NETRuntime-LICENSE.txt','licenses\NETRuntime-THIRD-PARTY-NOTICES.txt','licenses\NETDesktop-LICENSE.txt')
# SDK versions may leave optional desktop native sidecars instead of bundling them.
foreach($sidecar in @('D3DCompiler_47_cor3.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll')) {
    if(Test-Path -LiteralPath (Join-Path $dist $sidecar)){$files+=$sidecar}
}
foreach($file in $files){if(-not(Test-Path -LiteralPath (Join-Path $dist $file))){throw "Package input missing: $file"}}
Add-Type -AssemblyName System.IO.Compression
$manifest=@()
foreach($file in $files){$p=Join-Path $dist $file;$manifest += [ordered]@{file=$file.Replace('\','/');sha256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash;bytes=(Get-Item -LiteralPath $p).Length}}
$temporaryZip=$zipPath+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
$archive=[System.IO.Compression.ZipFile]::Open($temporaryZip,[System.IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in $files){[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $dist $file),('RONWG/'+$file.Replace('\','/')),[System.IO.Compression.CompressionLevel]::Optimal)|Out-Null}
    $entry=$archive.CreateEntry('RONWG/package-manifest.json');$writer=[System.IO.StreamWriter]::new($entry.Open(),[System.Text.UTF8Encoding]::new($false))
    try{$writer.Write((ConvertTo-Json -Depth 8 -InputObject ([ordered]@{version=$Version;platform='win-x64';selfContained=$true;builtAt=(Get-Date).ToString('o');files=$manifest})))}finally{$writer.Dispose()}
}finally{$archive.Dispose()}
Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
$hash=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath ($zipPath+'.sha256.txt') -Encoding ascii
Write-Output "Ready to send: $zipPath"
Write-Output "Size: $([math]::Round((Get-Item -LiteralPath $zipPath).Length/1MB,2)) MiB"
