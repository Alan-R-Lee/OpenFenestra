param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $localDotnet; $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $SkipBuild) {
    & $dotnetCommand build OpenFenestra.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}
$testExe = Join-Path $projectRoot 'src\OpenFenestra.App\bin\Release\net10.0-windows\OpenFenestra.exe'
$testRoot = Join-Path $projectRoot ('.qa\desktop-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$normalData = Join-Path $testRoot 'normal'
$normal = Start-Process -FilePath $testExe -ArgumentList @('--data-dir', ('"' + $normalData + '"'), '--integration-test') -WindowStyle Hidden -PassThru
try {
    if (-not $normal.WaitForExit(40000)) { throw 'Desktop integration test timed out' }
    $result = Get-Content -LiteralPath (Join-Path $normalData 'integration-result.json') -Raw | ConvertFrom-Json
    if (-not $result.passed) { throw ($result.checks -join "`n") }
    $result.checks | ForEach-Object { Write-Output $_ }
} finally { if (-not $normal.HasExited) { Stop-Process -Id $normal.Id -Force }; $normal.Dispose() }
$crashData = Join-Path $testRoot 'crash'
$crash = Start-Process -FilePath $testExe -ArgumentList @('--data-dir', ('"' + $crashData + '"'), '--integration-test', '--crash-test') -WindowStyle Hidden -PassThru
try {
    $readyPath = Join-Path $crashData 'integration-ready.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($crash.HasExited) { throw 'Crash test exited before initialization' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Crash test initialization timed out' }
        Start-Sleep -Milliseconds 200
    }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.processId -ne $crash.Id) { throw 'Crash test process identity mismatch' }
    Stop-Process -Id $crash.Id -Force
    $recoveryPath = Join-Path $crashData 'desktop-recovery.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while (Test-Path -LiteralPath $recoveryPath) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Desktop recovery guard did not finish' }
        Start-Sleep -Milliseconds 200
    }
    $inspectionPath = Join-Path $testRoot 'native-after-crash.json'
    $inspect = Start-Process -FilePath $testExe -ArgumentList @('--inspect-desktop', ('"' + $inspectionPath + '"')) -WindowStyle Hidden -PassThru
    if (-not $inspect.WaitForExit(10000)) { throw 'Desktop inspection timed out' }
    $state = Get-Content -LiteralPath $inspectionPath -Raw | ConvertFrom-Json
    if (-not $state.hasDesktop -or $state.nativeIconsVisible -ne $ready.originalVisible) { throw 'Original desktop visibility was not restored after the crash' }
    Write-Output 'PASS: Crash guard restored original Windows desktop icons'
    Write-Output "Results: $testRoot"
} finally { if (-not $crash.HasExited) { Stop-Process -Id $crash.Id -Force }; $crash.Dispose() }
