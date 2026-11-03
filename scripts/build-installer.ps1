param([string]$CompilerPath = '', [switch]$SkipPortableBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
if (-not $SkipPortableBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
& (Join-Path $PSScriptRoot 'verify-package.ps1')
if (-not $CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $CompilerPath = $command.Source }
    else {
        $CompilerPath = @((Join-Path $projectRoot '.tools\inno-setup\ISCC.exe'), "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe") |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $CompilerPath) { throw 'Install Inno Setup 6.5+ or pass -CompilerPath to ISCC.exe.' }
[xml]$project = Get-Content 'src/OpenFenestra.App/OpenFenestra.App.csproj' -Raw
$version = [string]$project.Project.PropertyGroup.Version | Where-Object { $_ }
$dotnet = if (Test-Path '.tools\dotnet\dotnet.exe') { Join-Path $projectRoot '.tools\dotnet\dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$light = Join-Path $projectRoot 'artifacts\OpenFenestra-light-win-x64'
& $dotnet publish src/OpenFenestra.App/OpenFenestra.App.csproj -c Release -r win-x64 --self-contained false -p:EnableNativeShellBridge=true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $light
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }
if ((Get-Item (Join-Path $light 'OpenFenestra.exe')).Length -gt 10MB) { throw 'Light app unexpectedly contains a runtime.' }
$full = Join-Path $projectRoot 'artifacts\OpenFenestra-win-x64'
foreach ($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md','docs')) {
    Copy-Item -LiteralPath (Join-Path $full $name) -Destination $light -Recurse -Force
}
$output = Join-Path $projectRoot 'artifacts'
foreach ($variant in @('Light','Offline')) {
    $payload = if ($variant -eq 'Light') { $light } else { $full }
    & $CompilerPath "/DAppVersion=$version" "/DVariant=$variant" "/DPayloadDir=$payload" "/DOutputDir=$output" 'installer/OpenFenestra.iss'
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $variant" }
}
$assets = @("OpenFenestra-$version-Setup-Light-x64.exe", "OpenFenestra-$version-Setup-Offline-x64.exe", 'OpenFenestra-win-x64.zip')
$lines = foreach ($name in $assets) {
    $file = Get-Item -LiteralPath (Join-Path $output $name)
    Write-Output "$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $name"
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
foreach ($name in $assets) { Get-Item -LiteralPath (Join-Path $output $name) | Select-Object Name,Length }
