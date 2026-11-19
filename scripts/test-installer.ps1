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
try {
    foreach ($variant in @('Light','Offline')) {
        $target = Join-Path $testRoot $variant
        $setup = Join-Path $projectRoot "artifacts\OpenFenestra-$version-Setup-$variant-x64.exe"
        $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="'+$target+'"'),('/GROUP="OpenFenestra-QA-'+(Split-Path -Leaf $testRoot)+'"'),'/TASKS=""',('/LOG="'+(Join-Path $testRoot "$variant-install.log")+'"')) -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Installer failed: $variant" }
        $app = Join-Path $target 'OpenFenestra.exe'
        if ((Get-Item -LiteralPath $app).VersionInfo.ProductVersion -notlike "$version*") { throw 'Installed app version mismatch.' }
        if (-not (Test-Path -LiteralPath (Join-Path $target 'FenestraShellBridge.dll'))) { throw 'Native bridge missing after installation.' }
        $markerDir = Join-Path $target 'data'
        New-Item -ItemType Directory -Path $markerDir -Force | Out-Null
        $marker = Join-Path $markerDir 'preserve-test.txt'
        [IO.File]::WriteAllText($marker, 'preserve user data')
        $probe = Start-Process -FilePath $app -ArgumentList @('--exit','--data-dir',('"'+(Join-Path $testRoot "$variant-probe")+'"')) -WindowStyle Hidden -Wait -PassThru
        if ($probe.ExitCode -ne 0) { throw "Installed application failed to load: $variant" }
        $uninstall = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot "$variant-uninstall.log")+'"')) -WindowStyle Hidden -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: $variant" }
        # -Wait 同时等待 Inno 的临时卸载子进程，再给文件系统留出清理时间。
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Test-Path -LiteralPath $app) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (Test-Path -LiteralPath $app) { throw 'Uninstall left the application executable behind.' }
        if (Test-Path -LiteralPath $installedKey) { throw 'Uninstall left the product registration behind.' }
        if ([IO.File]::ReadAllText($marker) -ne 'preserve user data') { throw 'Uninstall removed user data.' }
        Write-Output "PASS $variant install, app startup, uninstall and user-data preservation"
    }
}
catch {
    Write-Output "Installer validation failed. Diagnostics: $testRoot"
    Get-ChildItem -LiteralPath $testRoot -File -Filter '*.log' | ForEach-Object {
        Write-Output ("--- " + $_.Name + " (last 60 lines) ---")
        Get-Content -LiteralPath $_.FullName -Tail 60
    }
    throw
}
Write-Output "Installer validation: $testRoot"
