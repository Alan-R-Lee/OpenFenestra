param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E750B846-31E7-4FCF-926B-C40901F019B8}_is1'
if (Test-Path -LiteralPath $installedKey) { throw 'Run installer validation on a clean user profile; an installed product already owns this uninstall key.' }
$dotnet = if (Test-Path (Join-Path $projectRoot '.tools\dotnet\dotnet.exe')) { Join-Path $projectRoot '.tools\dotnet\dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
[xml]$project = Get-Content (Join-Path $projectRoot 'src\OpenFenestra.App\OpenFenestra.App.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$testRoot = Join-Path $projectRoot ('.qa\installer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
foreach ($variant in @('Light','Offline')) {
    $target = Join-Path $testRoot $variant
    $setup = Join-Path $projectRoot "artifacts\OpenFenestra-$version-Setup-$variant-x64.exe"
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/NORESTART','/SP-',('/DIR="'+$target+'"'),('/GROUP="OpenFenestra-QA-'+(Split-Path -Leaf $testRoot)+'"'),'/TASKS=""',('/LOG="'+(Join-Path $testRoot "$variant-install.log")+'"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000) -or $process.ExitCode -ne 0) { throw "Installer failed: $variant" }
    $app = Join-Path $target 'OpenFenestra.exe'
    if ((Get-Item -LiteralPath $app).VersionInfo.ProductVersion -notlike "$version*") { throw 'Installed app version mismatch.' }
    if (-not (Test-Path -LiteralPath (Join-Path $target 'FenestraShellBridge.dll'))) { throw 'Native bridge missing after installation.' }
    $markerDir = Join-Path $target 'data'
    New-Item -ItemType Directory -Path $markerDir -Force | Out-Null
    $marker = Join-Path $markerDir 'preserve-test.txt'
    [IO.File]::WriteAllText($marker, 'preserve user data')
    $probe = Start-Process -FilePath $app -ArgumentList @('--exit','--data-dir',('"'+(Join-Path $testRoot "$variant-probe")+'"')) -WindowStyle Hidden -PassThru
    if (-not $probe.WaitForExit(15000) -or $probe.ExitCode -ne 0) { throw "Installed application failed to load: $variant" }
    $uninstall = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/NORESTART',('/LOG="'+(Join-Path $testRoot "$variant-uninstall.log")+'"')) -WindowStyle Hidden -PassThru
    if (-not $uninstall.WaitForExit(30000) -or $uninstall.ExitCode -ne 0) { throw "Uninstall failed: $variant" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path -LiteralPath $app) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $app) { throw 'Uninstall left the application executable behind.' }
    if ([IO.File]::ReadAllText($marker) -ne 'preserve user data') { throw 'Uninstall removed user data.' }
    Write-Output "PASS $variant install, app startup, uninstall and user-data preservation"
}
Write-Output "Installer validation: $testRoot"
