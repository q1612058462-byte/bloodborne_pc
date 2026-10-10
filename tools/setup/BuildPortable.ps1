param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
trap {
    Write-Host ("Packaging failed at: " + $_.InvocationInfo.PositionMessage)
    Write-Host $_.ScriptStackTrace
    Write-Host $_.Exception.Message
    exit 1
}
$rootPath = [IO.Path]::GetFullPath($Root)
$packaging = Join-Path $rootPath 'packaging'
$stage = Join-Path $packaging 'windows-portable-payload'
$archive = Join-Path $packaging 'windows-portable-payload.zip'

function Copy-RequiredFile([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Missing payload file: $Source"
    }
    $parent = Split-Path -Parent $Destination
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-RequiredTree([string]$Source, [string]$Destination, [string[]]$ExcludeDirectories = @()) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Missing payload directory: $Source"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $sourceFull = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        $file = $_
        $relative = $file.FullName.Substring($sourceFull.Length).TrimStart('\')
        $parts = $relative -split '\\'
        $excluded = $false
        foreach ($part in $parts) {
            if ($ExcludeDirectories -contains $part) { $excluded = $true; break }
        }
        if ($excluded) { return }
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

New-Item -ItemType Directory -Force -Path $packaging | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-RequiredFile (Join-Path $rootPath 'out\bb-probe.exe') (Join-Path $stage 'out\bb-probe.exe')
Copy-RequiredFile (Join-Path $rootPath 'out\bb-gpu-capabilities.exe') (Join-Path $stage 'out\bb-gpu-capabilities.exe')
Copy-RequiredFile (Join-Path $rootPath 'out\nvngx_dlss.dll') (Join-Path $stage 'out\nvngx_dlss.dll')
Copy-RequiredFile (Join-Path $rootPath 'out\LICENSE-DLSS-SDK.txt') (Join-Path $stage 'out\LICENSE-DLSS-SDK.txt')
Copy-RequiredTree (Join-Path $rootPath 'fsr4_shaders') (Join-Path $stage 'fsr4_shaders')
if (Test-Path -LiteralPath (Join-Path $rootPath 'fsr4_411') -PathType Container) {
    Copy-RequiredTree (Join-Path $rootPath 'fsr4_411') (Join-Path $stage 'fsr4_411')
}
Copy-RequiredTree (Join-Path $rootPath 'runtime') (Join-Path $stage 'runtime')

# Keep the Python standard library needed by the launcher. Development-only third-party
# packages, documentation, tests, Tk and headers are excluded from the player package.
$pythonBase = Join-Path $rootPath 'python'
$pythonStage = Join-Path $stage 'python'
New-Item -ItemType Directory -Force -Path $pythonStage | Out-Null
foreach ($pythonFileName in @('python.exe', 'pythonw.exe', 'python3.dll', 'python313.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'LICENSE.txt')) {
    Copy-RequiredFile (Join-Path $pythonBase $pythonFileName) (Join-Path $pythonStage $pythonFileName)
}
Copy-RequiredTree (Join-Path $pythonBase 'DLLs') (Join-Path $pythonStage 'DLLs') @('__pycache__')
Copy-RequiredTree (Join-Path $pythonBase 'Lib') (Join-Path $pythonStage 'Lib') @(
    'site-packages', 'test', 'tests', '__pycache__', 'ensurepip', 'idlelib', 'tkinter',
    'turtledemo', 'venv', 'pydoc_data', 'unittest'
)

New-Item -ItemType Directory -Force -Path (Join-Path $stage 'scripts') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $rootPath 'scripts') -Filter '*.py' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage 'scripts') -Force
}
Copy-RequiredTree (Join-Path $rootPath 'patches') (Join-Path $stage 'patches')
Copy-RequiredFile (Join-Path $rootPath 'LICENSE') (Join-Path $stage 'LICENSE-bbport.txt')
Copy-RequiredFile (Join-Path $rootPath 'local_deps\shadp2p\client\shadPS4.exe') (Join-Path $stage 'shadp2p\client\shadPS4.exe')
$shadnetSource = Join-Path $rootPath 'local_deps\shadp2p\server'
$shadnetStage = Join-Path $stage 'shadp2p\server'
foreach ($relative in @(
    'icuuc.dll', 'Qt6Core.dll', 'Qt6HttpServer.dll', 'Qt6Network.dll', 'Qt6Sql.dll',
    'Qt6WebSockets.dll', 'shadnet.exe', 'vc_redist.x64.exe',
    'networkinformation\qnetworklistmanager.dll',
    'sqldrivers\qsqlibase.dll', 'sqldrivers\qsqlite.dll', 'sqldrivers\qsqlmimer.dll',
    'sqldrivers\qsqloci.dll', 'sqldrivers\qsqlodbc.dll', 'sqldrivers\qsqlpsql.dll',
    'tls\qcertonlybackend.dll', 'tls\qschannelbackend.dll'
)) {
    Copy-RequiredFile (Join-Path $shadnetSource $relative) (Join-Path $shadnetStage $relative)
}
Copy-RequiredFile (Join-Path $rootPath 'LICENSE') (Join-Path $stage 'shadp2p\client\LICENSE-GPL-2.0.txt')
Copy-RequiredFile (Join-Path $rootPath 'LICENSE') (Join-Path $stage 'shadp2p\server\LICENSE-GPL-2.0.txt')
Copy-RequiredFile (Join-Path $packaging 'SHADP2P_THIRD_PARTY.txt') (Join-Path $stage 'shadp2p\THIRD_PARTY.txt')
Copy-RequiredFile (Join-Path $packaging 'APP_VERSION.txt') (Join-Path $stage 'APP_VERSION.txt')
Copy-RequiredFile (Join-Path $packaging 'RELEASE_NOTES_zh-CN.txt') (Join-Path $stage 'RELEASE_NOTES_zh-CN.txt')

$appVersion = [IO.File]::ReadAllText((Join-Path $packaging 'APP_VERSION.txt')).Trim()
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid APP_VERSION.txt: $appVersion (expected MAJOR.MINOR.PATCH)"
}

$probePath = Join-Path $rootPath 'out\bb-probe.exe'
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $probeStream = [IO.File]::OpenRead($probePath)
    try {
        $probeHash = ([BitConverter]::ToString($sha256.ComputeHash($probeStream))).Replace('-', '')
    }
    finally {
        $probeStream.Dispose()
    }
}
finally {
    $sha256.Dispose()
}
$buildInfo = @(
    'Bloodborne PC Windows portable runtime'
    ('Package version: ' + $appVersion)
    'Upstream branch: yumlevi/bloodborne_pc windows-port'
    'Upstream commit: d7a53a5bf835484648dbff427d3061b8aa7e68da'
    'Local feature: configurable simultaneous keyboard, mouse and complete controller remapping'
    'Local feature: stable save root, validation, import/export and automatic backups'
    'Local feature: v1.09 trainer cheats (health, echoes and upgrade items), centered IME popup and detailed crash diagnostics'
    'Local feature: bundled FSR 4 v07 and NVIDIA DLSS models; optional user-provided FSR 4.1.1 assets'
    'Local feature: save filename normalisation and backed-up character renaming'
    'Local feature: original gothic launcher artwork and gamepad motion sensors'
    'Local feature: paired shadp2p/shadNet LAN core, portable network profile and LAN account registration'
    'Local preview: native bbport LAN room discovery, join/leave, Matching2 callbacks and peer signaling without launching shadPS4'
    'Linux-port backports: scheduler race fix, time-based texture GC and atomic shader/save writes'
    'Local feature: pre-launch game integrity checks and optional Old Hunters entitlement folder'
    ('bb-probe SHA256: ' + $probeHash)
    ('Packaged at: ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))
) -join "`r`n"
[IO.File]::WriteAllText((Join-Path $stage 'BUILD_INFO.txt'), $buildInfo + "`r`n", (New-Object Text.UTF8Encoding($false)))

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)

$payloadFiles = Get-ChildItem -LiteralPath $stage -Recurse -File
$uncompressed = ($payloadFiles | Measure-Object Length -Sum).Sum
$compressed = (Get-Item -LiteralPath $archive).Length
Write-Host ("Payload: {0} files, {1:N1} MB unpacked, {2:N1} MB compressed" -f $payloadFiles.Count, ($uncompressed / 1MB), ($compressed / 1MB))
