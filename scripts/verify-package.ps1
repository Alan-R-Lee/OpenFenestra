param([string]$ArchivePath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArchivePath) { $ArchivePath = Join-Path $projectRoot 'artifacts\OpenFenestra-win-x64.zip' }
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
$checksumPath = [IO.Path]::ChangeExtension($ArchivePath, '.sha256')
$expectedHash = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Package checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    $allowedRoots = @('OpenFenestra.exe','FenestraShellBridge.dll','README.md','LICENSE','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md','docs','licenses')
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName -replace '\\', '/'
        if (($name -split '/')[0] -notin $allowedRoots) { throw "Unexpected package content: $name" }
        if ($name -match '(^|/)(data|\.qa|\.tools|\.research|bin|obj)/|BootstrapProbe|RESEARCH\.md|\.(pdb|dmp|log)$') { throw "Development/private content in package: $name" }
        if ($name.StartsWith('docs/') -and -not $name.EndsWith('/') -and -not (Test-Path -LiteralPath (Join-Path $projectRoot $name) -PathType Leaf)) { throw "Stale documentation: $name" }
    }
    foreach ($name in @('OpenFenestra.exe','FenestraShellBridge.dll','README.md','LICENSE','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','SECURITY.md','docs/USAGE.md','docs/NATIVE_PROTOCOL.md','licenses/native-runtime/build-info.json')) {
        if (-not $zip.GetEntry($name)) { throw "Missing package file: $name" }
    }
    function Get-EntryHash([string]$Name) {
        $entry = $zip.GetEntry($Name)
        if (-not $entry) { throw "Missing runtime notice: $Name" }
        $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
        try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $stream.Dispose() }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('licenses/native-runtime/build-info.json').Open())
    try { $info = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ((Get-EntryHash 'FenestraShellBridge.dll') -ne $info.dllSHA256) { throw 'Native build information does not match the DLL.' }
    foreach ($notice in $info.noticeFiles) {
        if ((Get-EntryHash ('licenses/native-runtime/' + $notice.file)) -ne $notice.sha256) { throw "Runtime notice checksum mismatch: $($notice.file)" }
    }
    Write-Output "Package verified: $($zip.Entries.Count) entries, checksum, clean contents and native runtime notices."
} finally { $zip.Dispose() }
