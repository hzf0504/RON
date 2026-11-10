param([switch]$Offline)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$results=Join-Path $projectRoot ('tests\results\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $results -Force | Out-Null
foreach($harness in @('NativeHarness','DelayHarness')) {
    $harnessArgs=@((Join-Path $projectRoot 'build\native\RonWgProbe.dll'),(Join-Path $results ($harness+'.jsonl')))
    if($Offline){$harnessArgs+='--offline'}
    & (Join-Path $projectRoot "build\native-tests\$harness.exe") @harnessArgs | Tee-Object -FilePath (Join-Path $results ($harness+'.txt'))
    if($LASTEXITCODE -ne 0) { throw "$harness failed." }
}
& (Join-Path $projectRoot 'build\native-tests\LatencyHarness.exe') (Join-Path $projectRoot 'build\native\RonLatency.dll') (Join-Path $results 'latency') | Tee-Object -FilePath (Join-Path $results 'latency.txt')
if($LASTEXITCODE -ne 0){throw 'Latency collection tests failed.'}
$managedProject=if($Offline){'LogTests.csproj'}else{'ManagedTests.csproj'}
& dotnet run --project (Join-Path $projectRoot "tests\$managedProject") -c Release -- $projectRoot $results | Tee-Object -FilePath (Join-Path $results 'managed.txt')
if($LASTEXITCODE -ne 0) { throw 'Managed/injection tests failed.' }
Write-Output "Tests passed. Evidence: $results"
