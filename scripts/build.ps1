param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Runtime -ne 'win-x64') {
    throw 'OpenFenestra 0.3 native desktop releases support Windows 11 x64 only. ARM64 is not packaged with an x64 bridge. A managed-only build can use dotnet publish -p:EnableNativeShellBridge=false.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$outputDirectory = Join-Path $projectRoot "artifacts\OpenFenestra-$Runtime"
& $dotnetCommand publish src/OpenFenestra.App/OpenFenestra.App.csproj -c Release -r $Runtime --self-contained true -p:EnableNativeShellBridge=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$bridgePath = Join-Path $outputDirectory 'FenestraShellBridge.dll'
if (-not (Test-Path -LiteralPath $bridgePath -PathType Leaf)) { throw 'Published native desktop bridge is missing.' }
$bridgeBytes = [IO.File]::ReadAllBytes($bridgePath)
if ($bridgeBytes.Length -lt 64) { throw 'Published native desktop bridge is not a valid PE file.' }
$bridgePeOffset = [BitConverter]::ToInt32($bridgeBytes, 0x3c)
if ($bridgePeOffset -lt 0 -or $bridgePeOffset + 6 -gt $bridgeBytes.Length -or [BitConverter]::ToUInt32($bridgeBytes, $bridgePeOffset) -ne 0x00004550 -or [BitConverter]::ToUInt16($bridgeBytes, $bridgePeOffset + 4) -ne 0x8664) {
    throw 'Published native desktop bridge must be Windows x64.'
}
$nativeBuildInfoPath = Join-Path $outputDirectory 'licenses\native-runtime\build-info.json'
if (-not (Test-Path -LiteralPath $nativeBuildInfoPath -PathType Leaf)) { throw 'Published native runtime notices/build information are missing.' }
$nativeBuildInfo = Get-Content -LiteralPath $nativeBuildInfoPath -Raw | ConvertFrom-Json
if ($nativeBuildInfo.dllSHA256 -ne (Get-FileHash -LiteralPath $bridgePath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Published native runtime notices do not match the bridge DLL.' }
foreach ($notice in $nativeBuildInfo.noticeFiles) {
    $noticePath = Join-Path (Split-Path -Parent $nativeBuildInfoPath) $notice.file
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or (Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $notice.sha256) { throw "Published native runtime notice is missing or mismatched: $($notice.file)" }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $outputDirectory -Recurse -Force
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { (& $dotnetCommand nuget locals global-packages --list) -replace '^[^:]+:\s*','' }
foreach ($pack in @("microsoft.netcore.app.runtime.$Runtime", "microsoft.windowsdesktop.app.runtime.$Runtime")) {
    $packDirectory = Join-Path $packageRoot $pack
    $versionDirectory = Get-ChildItem -LiteralPath $packDirectory -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $versionDirectory) { throw "Runtime license directory missing: $packDirectory" }
    $licenseDirectory = Join-Path $outputDirectory "licenses\$pack"
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    $notices = Get-ChildItem -LiteralPath $versionDirectory.FullName -File | Where-Object { $_.Name -match '(?i)license|third.*notices' }
    if (-not $notices) { throw "Runtime notices missing: $pack" }
    $notices | Copy-Item -Destination $licenseDirectory -Force
}
$archive = Join-Path $projectRoot "artifacts\OpenFenestra-$Runtime.zip"
$distributionFiles = @('OpenFenestra.exe','FenestraShellBridge.dll','README.md','LICENSE','THIRD_PARTY_NOTICES.md','docs','licenses') | ForEach-Object { Join-Path $outputDirectory $_ }
Compress-Archive -LiteralPath $distributionFiles -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $projectRoot "artifacts\OpenFenestra-$Runtime.sha256"), "$checksum  OpenFenestra-$Runtime.zip`n")
Write-Output "Built: $archive"
