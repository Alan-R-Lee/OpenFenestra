param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\OpenFenestra'),
    [switch]$StartWithWindows,
    [switch]$DesktopIntegration,
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'The OpenFenestra 0.3 native desktop package requires Windows x64. ARM64 is not supported by this installer.'
}
$windowsBuild = [int](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber).CurrentBuildNumber
if ($windowsBuild -lt 22000) { throw 'The OpenFenestra 0.3 native desktop package requires Windows 11.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $projectRoot 'artifacts\OpenFenestra-win-x64'
$sourceExe = Join-Path $sourceDirectory 'OpenFenestra.exe'
$sourceBridge = Join-Path $sourceDirectory 'FenestraShellBridge.dll'
$needsBuild = -not (Test-Path -LiteralPath $sourceExe -PathType Leaf) -or -not (Test-Path -LiteralPath $sourceBridge -PathType Leaf)
if (-not $needsBuild) { $needsBuild = [version](Get-Item -LiteralPath $sourceExe).VersionInfo.FileVersion -lt [version]'0.3.0.0' }
if ($needsBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
$bridgeBytes = [IO.File]::ReadAllBytes($sourceBridge)
if ($bridgeBytes.Length -lt 64) { throw 'The native desktop bridge is not a valid PE file. Rebuild the portable package.' }
$bridgePeOffset = [BitConverter]::ToInt32($bridgeBytes, 0x3c)
if ($bridgePeOffset -lt 0 -or $bridgePeOffset + 6 -gt $bridgeBytes.Length -or [BitConverter]::ToUInt32($bridgeBytes, $bridgePeOffset) -ne 0x00004550 -or [BitConverter]::ToUInt16($bridgeBytes, $bridgePeOffset + 4) -ne 0x8664) {
    throw 'The installer requires a Windows x64 native desktop bridge. Rebuild the portable package.'
}
$installDirectory = [System.IO.Path]::GetFullPath($Destination)
if ($installDirectory.TrimEnd('\') -eq $sourceDirectory.TrimEnd('\') -or $installDirectory.StartsWith($sourceDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a destination outside the build output directory.' }
if ($NoStart -and ($StartWithWindows -or $DesktopIntegration)) { throw 'Startup and desktop options require starting the app once.' }
$installedExe = Join-Path $installDirectory 'OpenFenestra.exe'
$running = Get-Process OpenFenestra -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }
if ($running) { throw 'Exit OpenFenestra using its tray menu before updating the installed copy. Your layout is kept separately.' }
$installedBridge = Join-Path $installDirectory 'FenestraShellBridge.dll'
if (Test-Path -LiteralPath $installedBridge -PathType Leaf) {
    try {
        $bridgeProbe = [IO.File]::Open($installedBridge, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $bridgeProbe.Dispose()
    } catch { throw 'The installed native bridge is still in use. Disable desktop integration before updating; the installer will not restart Explorer automatically.' }
}
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
@('OpenFenestra.exe','FenestraShellBridge.dll','README.md','LICENSE','THIRD_PARTY_NOTICES.md','docs','licenses') | ForEach-Object {
    Copy-Item -LiteralPath (Join-Path $sourceDirectory $_) -Destination $installDirectory -Recurse -Force
}
if (-not $NoStart) {
    $arguments = @('--tray')
    if ($StartWithWindows) { $arguments += '--enable-startup' }
    if ($DesktopIntegration) { $arguments += '--desktop-mode' }
    Start-Process -FilePath $installedExe -ArgumentList $arguments -WindowStyle Hidden
}
Write-Output "Installed: $installedExe"
