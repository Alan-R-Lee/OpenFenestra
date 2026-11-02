$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnetCommand build OpenFenestra.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $dotnetCommand run --project tests/OpenFenestra.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
& $dotnetCommand run --project tests/OpenFenestra.Shell.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Shell data tests failed' }
$testData = Join-Path $projectRoot ('.qa\run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testData -Force | Out-Null
& $dotnetCommand run --project src/OpenFenestra.App -c Release --no-build -- --data-dir $testData --smoke-test
if ($LASTEXITCODE -ne 0) { throw "UI smoke tests failed; see $testData" }
$result = Get-Content -LiteralPath (Join-Path $testData 'smoke-result.json') -Raw | ConvertFrom-Json
if (-not $result.passed) { throw "UI smoke tests failed; see $testData" }
$result.checks | ForEach-Object { Write-Output $_ }
Write-Output "All tests passed. Results: $testData"
