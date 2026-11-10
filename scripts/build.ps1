param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$nativeOut = Join-Path $projectRoot 'build\native'
$dist = Join-Path $projectRoot 'dist\RonWgAssistant'
$tests = Join-Path $projectRoot 'build\native-tests'
New-Item -ItemType Directory -Path $nativeOut,$dist,$tests -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Visual C++ x64 tools are required.' }
$vc = (Get-ChildItem -LiteralPath (Join-Path $vsPath 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk = (Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$env:INCLUDE = "$vc\include;$kits\Include\$sdk\ucrt;$kits\Include\$sdk\shared;$kits\Include\$sdk\um;$kits\Include\$sdk\winrt"
$env:LIB = "$vc\lib\x64;$kits\Lib\$sdk\ucrt\x64;$kits\Lib\$sdk\um\x64"
$env:PATH = "$vc\bin\Hostx64\x64;$env:PATH"
$cl = Join-Path $vc 'bin\Hostx64\x64\cl.exe'
$common = @('/nologo','/std:c++17','/EHsc','/W4','/O2','/MT','/DUNICODE','/D_UNICODE')
Push-Location $nativeOut
try {
    & $cl @common /LD (Join-Path $projectRoot 'native\probe.cpp') "/Fe:$nativeOut\RonWgProbe.dll" /link /DYNAMICBASE /NXCOMPAT
    if ($LASTEXITCODE -ne 0) { throw 'Native module build failed.' }
    Copy-Item -LiteralPath "$nativeOut\RonWgProbe.dll" -Destination $dist -Force
    & $cl @common /LD (Join-Path $projectRoot 'native\latency.cpp') "/Fe:$nativeOut\RonLatency.dll" /link /DYNAMICBASE /NXCOMPAT
    if ($LASTEXITCODE -ne 0) { throw 'Latency module build failed.' }
    Copy-Item -LiteralPath "$nativeOut\RonLatency.dll" -Destination $dist -Force
} finally { Pop-Location }
Push-Location $tests
try {
    & $cl @common /LD (Join-Path $projectRoot 'native\tests\mock_eos.cpp') "/Fe:$tests\EOSSDK-Win64-Shipping.dll"
    if ($LASTEXITCODE -ne 0) { throw 'Mock SDK build failed.' }
    & $cl @common (Join-Path $projectRoot 'native\tests\harness.cpp') "$tests\EOSSDK-Win64-Shipping.lib" "/Fe:$tests\NativeHarness.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Native harness build failed.' }
    & $cl @common (Join-Path $projectRoot 'native\tests\harness.cpp') "$tests\EOSSDK-Win64-Shipping.lib" delayimp.lib "/Fe:$tests\DelayHarness.exe" /link /DELAYLOAD:EOSSDK-Win64-Shipping.dll
    if ($LASTEXITCODE -ne 0) { throw 'Delay import harness build failed.' }
    & $cl @common (Join-Path $projectRoot 'native\tests\remote_harness.cpp') "$tests\EOSSDK-Win64-Shipping.lib" delayimp.lib "/Fe:$tests\RemoteHarness.exe" /link /DELAYLOAD:EOSSDK-Win64-Shipping.dll
    if ($LASTEXITCODE -ne 0) { throw 'Remote injection harness build failed.' }
    & $cl @common (Join-Path $projectRoot 'native\tests\latency_harness.cpp') "/Fe:$tests\LatencyHarness.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Latency harness build failed.' }
} finally { Pop-Location }
$publishArgs = @('publish',(Join-Path $projectRoot 'src\Assistant\Assistant.csproj'),'-c','Release','-r','win-x64','--self-contained',([bool]$SelfContained).ToString().ToLower(),'-o',$dist,'-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=None')
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Assistant build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'compatibility.json') -Destination $dist -Force
if (Test-Path -LiteralPath (Join-Path $projectRoot 'README.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $dist -Force }
New-Item -ItemType Directory -Path (Join-Path $dist 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\Firewall.ps1'),(Join-Path $projectRoot 'scripts\Capture-Peer.ps1'),(Join-Path $projectRoot 'scripts\Export-Logs.ps1') -Destination (Join-Path $dist 'scripts') -Force
foreach($document in @('TESTER_GUIDE.md','LOG_FORMAT.md')){if(Test-Path -LiteralPath (Join-Path $projectRoot $document)){Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $dist -Force}}
$info=[ordered]@{version='0.3.0-experimental';selfContained=[bool]$SelfContained;framework='net8.0-windows';architecture='x64';sdk=(& dotnet --version);builtAt=(Get-Date).ToString('o');exeSha256=(Get-FileHash -LiteralPath (Join-Path $dist 'RonWgAssistant.exe')).Hash;probeSha256=(Get-FileHash -LiteralPath (Join-Path $dist 'RonWgProbe.dll')).Hash;latencySha256=(Get-FileHash -LiteralPath (Join-Path $dist 'RonLatency.dll')).Hash}
$info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'build-info.json') -Encoding utf8
if($SelfContained) {
    $runtimeConfig=Get-Content -LiteralPath (Join-Path $projectRoot 'src\Assistant\bin\Release\net8.0-windows\win-x64\RonWgAssistant.runtimeconfig.json') -Raw | ConvertFrom-Json
    $assets=Get-Content -LiteralPath (Join-Path $projectRoot 'src\Assistant\obj\project.assets.json') -Raw | ConvertFrom-Json
    $licenseDir=Join-Path $dist 'licenses';New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
    foreach($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $packageId=$framework.name.ToLower()+'.runtime.win-x64'
        $package=@($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {Join-Path $_ "$packageId\$($framework.version)"} | Where-Object {Test-Path -LiteralPath $_})[0]
        if(-not $package){throw "Runtime license source not found for $packageId"}
        $prefix=if($framework.name -eq 'Microsoft.NETCore.App'){'NETRuntime'}else{'NETDesktop'}
        foreach($licenseFile in @(Get-ChildItem -LiteralPath $package -File | Where-Object {$_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$'})) {
            $suffix=if($licenseFile.Name -like 'LICENSE*'){'LICENSE.txt'}else{'THIRD-PARTY-NOTICES.txt'}
            Copy-Item -LiteralPath $licenseFile.FullName -Destination (Join-Path $licenseDir "$prefix-$suffix") -Force
        }
    }
    $info.runtimeFrameworks=$runtimeConfig.runtimeOptions.includedFrameworks
    $info | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $dist 'build-info.json') -Encoding utf8
}
Write-Host "Built: $dist"
