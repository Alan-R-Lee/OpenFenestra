param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\OpenFenestra'),
    [switch]$StartWithWindows,
    [switch]$DesktopIntegration,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $projectRoot 'artifacts\OpenFenestra-win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'OpenFenestra.exe'))) { & (Join-Path $PSScriptRoot 'build.ps1') }
$installDirectory = [System.IO.Path]::GetFullPath($Destination)
if ($installDirectory.TrimEnd('\') -eq $sourceDirectory.TrimEnd('\')) { throw 'Choose a destination outside the build output directory.' }
if ($NoStart -and ($StartWithWindows -or $DesktopIntegration)) { throw 'Startup and desktop options require starting the app once.' }
$installedExe = Join-Path $installDirectory 'OpenFenestra.exe'
$running = Get-Process OpenFenestra -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }
if ($running) { throw 'Exit OpenFenestra using its tray menu before updating the installed copy. Your layout is kept separately.' }
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
@('OpenFenestra.exe','README.md','LICENSE','THIRD_PARTY_NOTICES.md','docs','licenses') | ForEach-Object {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $_) -Destination $installDirectory -Recurse -Force
}
if (-not $NoStart) {
    $arguments = @('--tray')
    if ($StartWithWindows) { $arguments += '--enable-startup' }
    if ($DesktopIntegration) { $arguments += '--desktop-mode' }
    Start-Process -FilePath $installedExe -ArgumentList $arguments -WindowStyle Hidden
}
Write-Output "Installed: $installedExe"
