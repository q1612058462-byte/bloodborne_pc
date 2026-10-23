param(
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [string]$OutputDirectory = '',
    [string]$BbscPackage = '',
    [string]$BbscDirectory = '',
    [string]$BbscPatchedDirectory = '',
    [string]$EmbeddedNetExe = ''
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
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { $packaging }
$stage = Join-Path $outputRoot 'windows-portable-payload'
$archive = Join-Path $outputRoot 'windows-portable-payload.zip'

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
if ($OutputDirectory) {
    if (Test-Path -LiteralPath $stage) { throw "Output staging directory already exists: $stage" }
    if (Test-Path -LiteralPath $archive) { throw "Output archive already exists: $archive" }
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
}
else {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-RequiredFile (Join-Path $rootPath 'out\bb-probe.exe') (Join-Path $stage 'out\bb-probe.exe')
Copy-RequiredFile (Join-Path $rootPath 'out\bb-gpu-capabilities.exe') (Join-Path $stage 'out\bb-gpu-capabilities.exe')
Copy-RequiredFile (Join-Path $rootPath 'out\nvngx_dlss.dll') (Join-Path $stage 'out\nvngx_dlss.dll')
Copy-RequiredFile (Join-Path $rootPath 'out\LICENSE-DLSS-SDK.txt') (Join-Path $stage 'out\LICENSE-DLSS-SDK.txt')
Copy-RequiredTree (Join-Path $rootPath 'fsr4_shaders') (Join-Path $stage 'fsr4_shaders')
if (Test-Path -LiteralPath (Join-Path $rootPath 'fsr4_411') -PathType Container) {
    Copy-RequiredTree (Join-Path $rootPath 'fsr4_411') (Join-Path $stage 'fsr4_411')
}
# Never pick up a developer's manually installed BBSC files by accident.
# An offline bundle must use the exact pinned official BBport release below.
Copy-RequiredTree (Join-Path $rootPath 'runtime') (Join-Path $stage 'runtime') @('bbsc')
if ($BbscPackage -and $BbscDirectory) { throw 'Choose either BbscPackage or BbscDirectory, not both.' }
if ($BbscPackage) {
    $bbscPath = [IO.Path]::GetFullPath($BbscPackage)
    if (-not (Test-Path -LiteralPath $bbscPath -PathType Leaf)) { throw "Missing BBSC package: $bbscPath" }
    $expectedBbscHash = '001EFC0A03B47BD6B2660C598FD6D172D2AAAE3201A4982134D1F215918ACD3C'
    $actualBbscHash = (Get-FileHash -LiteralPath $bbscPath -Algorithm SHA256).Hash
    if ($actualBbscHash -ne $expectedBbscHash) {
        throw "BBSC package hash mismatch: $bbscPath"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bbscArchive = [IO.Compression.ZipFile]::OpenRead($bbscPath)
    try {
        $bbscFiles = @('LICENSE.txt', 'NOTICE.txt', 'SUPPORTERS.txt', 'mod.json', 'CREDITS.txt')
        foreach ($name in $bbscFiles) {
            $entryName = 'BBSC-0.0.2-BBport/bbsc/' + $name
            $entry = $bbscArchive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $entryName } | Select-Object -First 1
            if ($null -eq $entry -or $entry.Length -gt 20MB) { throw "BBSC package missing or invalid: $entryName" }
            $destination = Join-Path $stage ('runtime\bbsc\' + $name)
            $parent = Split-Path -Parent $destination
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination)
        }
    }
    finally { $bbscArchive.Dispose() }
    if (-not $BbscPatchedDirectory) { throw 'BBSC 0.0.2 package requires the pinned 1.8.7 patched client build.' }
    $patchedRoot = [IO.Path]::GetFullPath($BbscPatchedDirectory)
    $patchedHashes = @{
        'bbsc-launch.exe' = 'E015873684A6777EEED54306F80EA025AE874F9F6B37FC47C898CC6FF5AF8390'
        'bbsc.dll' = '638D61538022B37979A4ED5D0AD0EECC23B99C6AC5D6D8056755F08D1B8C95E2'
    }
    foreach ($name in $patchedHashes.Keys) {
        $source = Join-Path $patchedRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $patchedHashes[$name]) {
            throw "BBSC 1.8.7 patched component missing or hash mismatch: $source"
        }
        Copy-RequiredFile $source (Join-Path $stage ('runtime\bbsc\' + $name))
    }
    Copy-RequiredFile (Join-Path $packaging 'BBSC_THIRD_PARTY.txt') (Join-Path $stage 'runtime\bbsc\SOURCE_AND_LICENSE.txt')
}
if ($BbscDirectory) {
    # A previously installed offline bundle is also an acceptable source, but
    # only when every pinned upstream component matches byte-for-byte.
    $bbscRoot = [IO.Path]::GetFullPath($BbscDirectory)
    $expected = @{
        'bbsc-launch.exe' = 'E015873684A6777EEED54306F80EA025AE874F9F6B37FC47C898CC6FF5AF8390'
        'bbsc.dll' = '638D61538022B37979A4ED5D0AD0EECC23B99C6AC5D6D8056755F08D1B8C95E2'
        'LICENSE.txt' = '8177F97513213526DF2CF6184D8FF986C675AFB514D4E68A404010521B880643'
        'NOTICE.txt' = '9919B68F5E5F4F80D2B316C7DAE40C45D0D43CE6274670D67DE655FB11F01F07'
        'SUPPORTERS.txt' = '82C82F554EAD1AEF9639EE42596A34686BAA508D4B9A63566C35F9B1F5DBD6A0'
        'mod.json' = 'CCEA635CAF411F56859070D971F5920FE60462DA915F291B1F9720A285AE7321'
    }
    foreach ($name in $expected.Keys) {
        $source = Join-Path $bbscRoot $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expected[$name]) {
            throw "BBSC installed component missing or hash mismatch: $source"
        }
        Copy-RequiredFile $source (Join-Path $stage ('runtime\bbsc\' + $name))
    }
    Copy-RequiredFile (Join-Path $bbscRoot 'CREDITS.txt') (Join-Path $stage 'runtime\bbsc\CREDITS.txt')
    Copy-RequiredFile (Join-Path $packaging 'BBSC_THIRD_PARTY.txt') (Join-Path $stage 'runtime\bbsc\SOURCE_AND_LICENSE.txt')
}

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
$embeddedNetSource = if ($EmbeddedNetExe) { [IO.Path]::GetFullPath($EmbeddedNetExe) } else {
    Join-Path $rootPath 'tools\embedded_net\bbport-net.exe'
}
Copy-RequiredFile $embeddedNetSource (Join-Path $stage 'net\bbport-net.exe')
Copy-RequiredFile (Join-Path $rootPath 'tools\embedded_net\NOTICE.txt') (Join-Path $stage 'licenses\embedded-net-NOTICE.txt')
Copy-RequiredFile (Join-Path $rootPath 'tools\embedded_net\TAILSCALE_LICENSE.txt') (Join-Path $stage 'licenses\tailscale-BSD-3-Clause.txt')

# Licenses for the complete shadp2p/shadNet stack and its dynamically loaded
# Windows dependencies.  The MSYS2 prefix is discovered rather than assumed so
# setup.bat remains portable between C: and D: installations.
$licenseStage = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenseStage | Out-Null
Copy-RequiredFile (Join-Path $rootPath 'gpu\third_party\wepoll\LICENSE') (Join-Path $licenseStage 'wepoll-LICENSE.txt')
Copy-RequiredFile (Join-Path $rootPath 'gpu\third_party\httplib\LICENSE') (Join-Path $licenseStage 'cpp-httplib-LICENSE.txt')
Copy-RequiredTree (Join-Path $rootPath 'packaging\windows\licenses') $licenseStage
$msysPrefixes = @($env:MSYSTEM_PREFIX, 'C:\msys64\clang64', 'D:\msys64\clang64') |
    Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } |
    Select-Object -Unique
foreach ($packageName in @('protobuf', 'abseil-cpp', 'openssl', 'miniupnpc', 'zlib')) {
    foreach ($prefix in $msysPrefixes) {
        $license = Join-Path $prefix ('share\licenses\' + $packageName + '\LICENSE')
        if (Test-Path -LiteralPath $license -PathType Leaf) {
            Copy-Item -LiteralPath $license -Destination (Join-Path $licenseStage ($packageName + '-LICENSE.txt')) -Force
            break
        }
    }
}
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
Copy-RequiredFile (Join-Path $rootPath 'LICENSE') (Join-Path $stage 'shadp2p\server\LICENSE-GPL-2.0.txt')
Copy-RequiredFile (Join-Path $packaging 'SHADP2P_THIRD_PARTY.txt') (Join-Path $stage 'shadp2p\THIRD_PARTY.txt')
Copy-RequiredFile (Join-Path $packaging 'APP_VERSION.txt') (Join-Path $stage 'APP_VERSION.txt')
Copy-RequiredFile (Join-Path $packaging 'RELEASE_NOTES_PLAYER_zh-CN.txt') (Join-Path $stage 'RELEASE_NOTES_zh-CN.txt')
Copy-RequiredFile (Join-Path $packaging 'ONLINE_SERVICE.txt') (Join-Path $stage 'ONLINE_SERVICE.txt')

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
    'Local feature: shader cache status and same-version/same-GPU ZIP merge; optional frame/GPU diagnostics'
    'Local feature: save filename normalisation and backed-up character renaming'
    'Local feature: original gothic launcher artwork and gamepad motion sensors'
    'Local feature: native bbport LAN core with bundled shadNet server and LAN account registration'
    'Local feature: native bbport online play using the complete shadp2p network, NP, WebAPI, Matching2 and signaling stack'
    'Local feature: bundled userspace tsnet helper for isolated self-hosted Headscale connectivity (experimental)'
    'Linux-port backports: scheduler race fix, time-based texture GC and atomic shader/save writes'
    'Local feature: pre-launch game integrity checks and optional Old Hunters entitlement folder'
    ('Experimental feature: isolated BBSC seamless co-op lab adapter; original online mode remains available; BBSC offline components bundled: ' + [bool]($BbscPackage -or $BbscDirectory))
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
