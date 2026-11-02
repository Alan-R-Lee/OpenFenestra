param(
    [string]$OutputDirectory = '',
    [ValidateSet('auto', 'msvc', 'mingw')][string]$Compiler = 'auto',
    [string]$CompilerPath = '',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
function Get-NativeFileSha256([string]$Path) {
    $nativeHashStream = [IO.File]::OpenRead($Path)
    $nativeHashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($nativeHashAlgorithm.ComputeHash($nativeHashStream)).Replace('-', '').ToLowerInvariant() }
    finally { $nativeHashAlgorithm.Dispose(); $nativeHashStream.Dispose() }
}
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\native-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sourceDirectory = Join-Path $projectRoot 'native\ShellBridge'
$dll = Join-Path $OutputDirectory 'FenestraShellBridge.dll'
$testExecutable = Join-Path $OutputDirectory 'NativeShellBridge.SelfTest.exe'
$vcvars = $null
if ($Compiler -ne 'mingw') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vcvars = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
    }
    if ($Compiler -eq 'msvc' -and -not $vcvars) { throw 'MSVC x64 C++ tools were not found by vswhere.' }
}
if ($vcvars) {
    # Keep vcvars and compilation in the same shell. No environment scraping and
    # no shell-generated file operations; the temporary script only invokes cl.
    $nativeBatch = Join-Path $OutputDirectory 'build-native-msvc.cmd'
    $nativeCommands = @(
        '@echo off',
        'chcp 65001 >nul',
        ('call "{0}" >nul' -f $vcvars),
        'if errorlevel 1 exit /b %errorlevel%',
        ('cl /nologo /std:c++17 /permissive- /O2 /MT /W4 /WX /EHsc /LD "{0}" /Fo"{1}" /Fe"{2}" /link user32.lib comctl32.lib shell32.lib shlwapi.lib ole32.lib oleaut32.lib uuid.lib /MACHINE:X64' -f (Join-Path $sourceDirectory 'Bridge.cpp'), (Join-Path $OutputDirectory 'Bridge.obj'), $dll),
        'if errorlevel 1 exit /b %errorlevel%'
    )
    if ($SelfTest) {
        $nativeCommands += ('cl /nologo /std:c++17 /permissive- /O2 /MT /W4 /WX /EHsc "{0}" /Fo"{1}" /Fe"{2}" /link user32.lib comctl32.lib ole32.lib uuid.lib /MACHINE:X64' -f (Join-Path $sourceDirectory 'SelfTest.cpp'), (Join-Path $OutputDirectory 'SelfTest.obj'), $testExecutable)
        $nativeCommands += 'if errorlevel 1 exit /b %errorlevel%'
    }
    $nativeCommands += 'exit /b 0'
    [IO.File]::WriteAllLines($nativeBatch, $nativeCommands, [Text.Encoding]::Default)
    & $env:ComSpec /d /c $nativeBatch
    if ($LASTEXITCODE -ne 0) { throw 'MSVC native bridge build failed.' }
} else {
    $gpp = $CompilerPath
    if (-not $gpp) {
        $candidate = Get-Command 'g++.exe' -ErrorAction SilentlyContinue
        if ($candidate) { $gpp = $candidate.Source }
    }
    if (-not $gpp -or -not (Test-Path -LiteralPath $gpp)) { throw 'Install MSVC x64 tools or MinGW-w64 g++, or pass -CompilerPath.' }
    $target = & $gpp -dumpmachine
    if ($LASTEXITCODE -ne 0 -or $target -notmatch '^x86_64-.*mingw') { throw "Expected an x86-64 MinGW compiler; got '$target'." }
    $common = @('-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', '-static', '-static-libgcc', '-static-libstdc++')
    # MinGW's linker does not reliably decode non-ASCII output arguments. The
    # Windows working directory remains Unicode; give ld only an ASCII basename.
    Push-Location -LiteralPath $OutputDirectory
    try {
        & $gpp @common -shared (Join-Path $sourceDirectory 'Bridge.cpp') -o 'FenestraShellBridge.dll' -lole32 -loleaut32 -luser32 -lcomctl32 -lshell32 -lshlwapi -luuid
        if ($LASTEXITCODE -ne 0) { throw 'MinGW native bridge build failed.' }
        if ($SelfTest) {
            & $gpp @common -municode (Join-Path $sourceDirectory 'SelfTest.cpp') -o 'NativeShellBridge.SelfTest.exe' -lole32 -luser32 -lcomctl32 -luuid
            if ($LASTEXITCODE -ne 0) { throw 'MinGW native self-test build failed.' }
        }
    } finally { Pop-Location }
}
$nativeBytes = [IO.File]::ReadAllBytes($dll)
$nativePeOffset = [BitConverter]::ToInt32($nativeBytes, 0x3c)
if ([BitConverter]::ToUInt16($nativeBytes, $nativePeOffset + 4) -ne 0x8664) { throw 'The produced DLL is not Windows x64.' }
Write-Output "Built x64 bridge: $dll"
if ($SelfTest) {
    $nativeTestHash = Get-NativeFileSha256 $dll
    $nativeTestCache = Join-Path $OutputDirectory "selftest-cache\$nativeTestHash"
    New-Item -ItemType Directory -Path $nativeTestCache -Force | Out-Null
    $nativeTestDll = Join-Path $nativeTestCache 'FenestraShellBridge.dll'
    Copy-Item -LiteralPath $dll -Destination $nativeTestDll -Force
    & $testExecutable $nativeTestDll
    if ($LASTEXITCODE -ne 0) { throw 'Isolated native bridge self-test failed.' }
}

# Runtime notices accompany the DLL, including when this script is used without
# dotnet publish. Copy the actual MinGW distribution's texts rather than assuming
# the tracked reference toolchain's notices apply to every compiler installation.
$nativeNoticeSource = Join-Path $projectRoot 'docs\licenses\native-runtime'
$nativeNoticeOutput = Join-Path $OutputDirectory 'licenses\native-runtime'
New-Item -ItemType Directory -Path $nativeNoticeOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeNoticeSource 'README.md'), (Join-Path $nativeNoticeSource 'SOURCES.md') -Destination $nativeNoticeOutput -Force
$nativeNoticeMappings = @()
if ($vcvars) {
    $nativeCompilerFamily = 'msvc'
    $nativeCompilerVersion = 'MSVC Visual Studio toolchain'
    $nativeToolsVersionFile = Join-Path (Split-Path -Parent $vcvars) 'Microsoft.VCToolsVersion.default.txt'
    if (Test-Path -LiteralPath $nativeToolsVersionFile) { $nativeCompilerVersion = 'MSVC toolset ' + ([IO.File]::ReadAllText($nativeToolsVersionFile).Trim()) }
    $nativeLinkage = '/MT (static Microsoft CRT and C++ runtime)'
    foreach ($noticeName in @('Microsoft-STL-LICENSE.txt', 'Microsoft-STL-NOTICE.txt')) {
        $nativeNoticeMappings += [pscustomobject]@{
            Source = Join-Path $nativeNoticeSource (Join-Path 'msvc' $noticeName)
            Name = $noticeName
            Origin = 'microsoft/STL e1ed302683e5e822f663fbb0f44cee7e9ddc6af0/' + ($noticeName -replace '^Microsoft-STL-', '')
        }
    }
} else {
    $nativeCompilerFamily = 'mingw'
    $nativeCompilerVersion = (& $gpp '--version' | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the MinGW compiler version for the runtime notices.' }
    $nativeLinkage = '-static -static-libgcc -static-libstdc++'
    $nativeInstalledLicenses = $env:OPENFENESTRA_MINGW_LICENSE_ROOT
    if (-not $nativeInstalledLicenses) {
        $nativeCompilerPrefix = Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($gpp)))
        $nativeInstalledLicenses = Join-Path $nativeCompilerPrefix 'share\licenses'
    }
    $nativeWinpthreadNotice = 'winpthreads\COPYING'
    if (-not (Test-Path -LiteralPath (Join-Path $nativeInstalledLicenses $nativeWinpthreadNotice) -PathType Leaf)) { $nativeWinpthreadNotice = 'libwinpthread\COPYING' }
    $nativeInstalledMappings = @(
        @('GPL-3.0.txt', 'gcc-libs\COPYING3'),
        @('GCC-Runtime-Library-Exception-3.1.txt', 'gcc-libs\COPYING.RUNTIME'),
        @('MSYS2-GCC-runtime-README.txt', 'gcc-libs\README'),
        @('MinGW-w64-runtime.txt', 'crt\COPYING.MinGW-w64-runtime.txt'),
        @('MinGW-w64-CRT-COPYING.txt', 'crt\COPYING'),
        @('winpthreads-COPYING.txt', $nativeWinpthreadNotice)
    )
    foreach ($mapping in $nativeInstalledMappings) {
        $nativeNoticePath = Join-Path $nativeInstalledLicenses $mapping[1]
        if (-not (Test-Path -LiteralPath $nativeNoticePath -PathType Leaf)) {
            throw "The MinGW runtime notice is missing: $($mapping[1]). Set OPENFENESTRA_MINGW_LICENSE_ROOT to the toolchain's original runtime license directory; see docs/licenses/native-runtime/README.md."
        }
        $nativeNoticeMappings += [pscustomobject]@{ Source = $nativeNoticePath; Name = $mapping[0]; Origin = 'toolchain/share/licenses/' + ($mapping[1] -replace '\\', '/') }
    }
    $nativeExceptionText = [IO.File]::ReadAllText((Join-Path $nativeInstalledLicenses 'gcc-libs\COPYING.RUNTIME'))
    if ($nativeExceptionText -notmatch 'GCC RUNTIME LIBRARY EXCEPTION' -or $nativeExceptionText -notmatch 'Version 3\.1') {
        throw 'The GCC runtime license set must include the complete GCC Runtime Library Exception 3.1.'
    }
}
$nativeSelectedNoticeOutput = Join-Path $nativeNoticeOutput $nativeCompilerFamily
New-Item -ItemType Directory -Path $nativeSelectedNoticeOutput -Force | Out-Null
$nativeNoticeRecords = @()
foreach ($mapping in $nativeNoticeMappings) {
    $nativeCopiedNotice = Join-Path $nativeSelectedNoticeOutput $mapping.Name
    Copy-Item -LiteralPath $mapping.Source -Destination $nativeCopiedNotice -Force
    $nativeNoticeRecords += [pscustomobject]@{
        file = $nativeCompilerFamily + '/' + $mapping.Name
        sha256 = Get-NativeFileSha256 $nativeCopiedNotice
        origin = $mapping.Origin
    }
}
$nativeBuildInfo = [ordered]@{
    schemaVersion = 1
    compilerFamily = $nativeCompilerFamily
    compilerVersion = $nativeCompilerVersion
    targetArchitecture = 'windows-x64'
    linkage = $nativeLinkage
    applicationSourceLicense = 'MIT'
    dllSHA256 = Get-NativeFileSha256 $dll
    noticeFiles = @($nativeNoticeRecords)
}
$nativeNoticeEncoding = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $nativeNoticeOutput 'build-info.json'), (($nativeBuildInfo | ConvertTo-Json -Depth 5) + "`n"), $nativeNoticeEncoding)
$nativeNoticeChecksums = ($nativeNoticeRecords | ForEach-Object { $_.sha256 + '  ' + $_.file }) -join "`n"
[IO.File]::WriteAllText((Join-Path $nativeNoticeOutput 'SHA256SUMS'), ($nativeNoticeChecksums + "`n"), $nativeNoticeEncoding)
Write-Output "Runtime notices: $nativeNoticeOutput ($nativeCompilerFamily)"
