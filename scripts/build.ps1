param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$outputDirectory = Join-Path $projectRoot "artifacts\OpenFenestra-$Runtime"
& $dotnetCommand publish src/OpenFenestra.App/OpenFenestra.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
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
Compress-Archive -Path (Join-Path $outputDirectory '*') -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $projectRoot "artifacts\OpenFenestra-$Runtime.sha256"), "$checksum  OpenFenestra-$Runtime.zip`n")
Write-Output "Built: $archive"
