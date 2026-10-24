param(
    [Parameter(Mandatory = $true)]
    [string] $OverlayServerIp,
    [string] $SeamlessOverlayServerIp = '',
    [string] $XboxModZip = '',
    [string] $PayloadZip = '',
    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($XboxModZip -and -not (Test-Path -LiteralPath $XboxModZip -PathType Leaf)) {
    throw "XboxModZip does not exist: $XboxModZip"
}
$xboxModSha256 = if ($XboxModZip) { (Get-FileHash -LiteralPath $XboxModZip -Algorithm SHA256).Hash } else { '' }
$address = $null
if (-not [System.Net.IPAddress]::TryParse($OverlayServerIp, [ref]$address) -or
    $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'OverlayServerIp must be an IPv4 address.'
}
$octets = $address.GetAddressBytes()
if ($octets[0] -ne 100 -or $octets[1] -lt 64 -or $octets[1] -gt 127) {
    throw 'OverlayServerIp must be in the Headscale 100.64.0.0/10 range.'
}
if ($SeamlessOverlayServerIp) {
    $seamlessAddress = $null
    if (-not [System.Net.IPAddress]::TryParse($SeamlessOverlayServerIp, [ref]$seamlessAddress) -or
        $seamlessAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'SeamlessOverlayServerIp must be an IPv4 address.'
    }
    $seamlessOctets = $seamlessAddress.GetAddressBytes()
    if ($seamlessOctets[0] -ne 100 -or $seamlessOctets[1] -lt 64 -or $seamlessOctets[1] -gt 127) {
        throw 'SeamlessOverlayServerIp must be in the Headscale 100.64.0.0/10 range.'
    }
    if ($seamlessAddress.Equals($address)) {
        throw 'SeamlessOverlayServerIp must use a separate server node from OverlayServerIp.'
    }
}

$sourceZip = if ($PayloadZip) { [IO.Path]::GetFullPath($PayloadZip) } else {
    Join-Path $root 'packaging\windows-portable-payload.zip'
}
$dist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist' }
$version = [IO.File]::ReadAllText((Join-Path $root 'packaging\APP_VERSION.txt')).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid package version: $version" }
$remoteZip = Join-Path $dist "windows-portable-payload-online-v$version.zip"
$output = Join-Path $dist "Bloodborne-PC-Online-v$version.exe"
$compileOutput = Join-Path $dist 'bbport-online-launcher.exe'
if (-not (Test-Path -LiteralPath $sourceZip)) { throw "Missing payload: $sourceZip" }
if (Test-Path -LiteralPath $remoteZip) { throw "Refusing to overwrite: $remoteZip" }
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite: $output" }
if (Test-Path -LiteralPath $compileOutput) { throw "Refusing to overwrite: $compileOutput" }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath $sourceZip -Destination $remoteZip

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Set-ZipText([System.IO.Compression.ZipArchive]$Archive, [string]$Name, [string]$Content) {
    $old = $Archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $Name } | Select-Object -First 1
    if ($null -eq $old) { throw "The existing payload lacks $Name." }
    $old.Delete()
    $entry = $Archive.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
    $bytes = ([System.Text.UTF8Encoding]::new($false)).GetBytes($Content)
    $stream = $entry.Open()
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}
$zip = [System.IO.Compression.ZipFile]::Open($remoteZip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    # A pre-existing payload may contain the retired third-party installer.
    foreach ($entry in @($zip.Entries)) {
        if ($entry.FullName.Replace('\', '/').StartsWith('tools/dlss5/', [StringComparison]::OrdinalIgnoreCase)) {
            $entry.Delete()
        }
    }
    $probeEntry = $zip.GetEntry('out/bb-probe.exe')
    $buildEntry = $zip.GetEntry('BUILD_INFO.txt')
    if ($null -eq $probeEntry -or $null -eq $buildEntry) {
        throw 'The payload lacks its core executable or BUILD_INFO.txt.'
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $probeStream = $probeEntry.Open()
        try { $probeHash = [BitConverter]::ToString($sha.ComputeHash($probeStream)).Replace('-', '') }
        finally { $probeStream.Dispose() }
    }
    finally { $sha.Dispose() }
    $buildReader = New-Object System.IO.StreamReader($buildEntry.Open(), [System.Text.Encoding]::UTF8)
    try { $buildInfo = $buildReader.ReadToEnd() }
    finally { $buildReader.Dispose() }
    $hashLine = '(?m)^bb-probe SHA256: [0-9A-Fa-f]{64}(?=\r?$)'
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($buildInfo, $hashLine)) {
        throw 'BUILD_INFO.txt lacks a valid bb-probe SHA256 line.'
    }
    $buildInfo = [System.Text.RegularExpressions.Regex]::Replace(
        $buildInfo, $hashLine, ('bb-probe SHA256: ' + $probeHash))
    Set-ZipText $zip 'BUILD_INFO.txt' $buildInfo
    $data = @"
# Remote online deployment. Game traffic is always carried by the embedded
# Headscale overlay; 127.0.0.2 is the local userspace bridge, not the server.
server=127.0.0.2:31313
portal=https://82.157.168.20
control=https://82.157.168.20
overlay_server=$OverlayServerIp
seamless_overlay_server=$SeamlessOverlayServerIp
xbox_mod_url=$(if ($XboxModZip) { 'https://82.157.168.20/windows/mods/xbox-prompts.zip' })
xbox_mod_sha256=$xboxModSha256
"@
    Set-ZipText $zip 'ONLINE_SERVICE.txt' ($data + "`n")
    Set-ZipText $zip 'APP_VERSION.txt' ($version + "`n")
    Set-ZipText $zip 'RELEASE_NOTES_zh-CN.txt' ([IO.File]::ReadAllText((Join-Path $root 'packaging\RELEASE_NOTES_PLAYER_zh-CN.txt')))
    Set-ZipText $zip 'scripts/run_game.py' ([IO.File]::ReadAllText((Join-Path $root 'scripts\run_game.py')))
}
finally { $zip.Dispose() }

$verifiedZip = [System.IO.Compression.ZipFile]::OpenRead($remoteZip)
try {
    $expectedEntry = $verifiedZip.GetEntry('BUILD_INFO.txt')
    $actualEntry = $verifiedZip.GetEntry('out/bb-probe.exe')
    if ($null -eq $expectedEntry -or $null -eq $actualEntry) { throw 'Packaged core verification files are missing.' }
    $reader = New-Object System.IO.StreamReader($expectedEntry.Open(), [System.Text.Encoding]::UTF8)
    try { $info = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $coreStream = $actualEntry.Open()
        try { $verifiedHash = [BitConverter]::ToString($sha.ComputeHash($coreStream)).Replace('-', '') }
        finally { $coreStream.Dispose() }
    }
    finally { $sha.Dispose() }
    if (-not $info.Contains('bb-probe SHA256: ' + $verifiedHash)) {
        throw 'Packaged core hash does not match BUILD_INFO.txt.'
    }
    if ($verifiedZip.Entries | Where-Object {
            $_.FullName.Replace('\', '/').StartsWith('tools/dlss5/', [StringComparison]::OrdinalIgnoreCase)
        }) { throw 'Retired third-party installer must not be packaged.' }
}
finally { $verifiedZip.Dispose() }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw 'The .NET Framework C# compiler was not found.' }

$compileArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    "/out:$compileOutput",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Security.dll',
    '/r:System.Web.Extensions.dll', '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll',
    "/resource:$remoteZip,bbport_payload.zip",
    "/resource:$(Join-Path $root 'scripts\bbport_settings_table.inc'),bbport_settings_table.inc",
    "/resource:$(Join-Path $root 'tools\setup\assets\launcher-background.png'),bbport_launcher_background.png",
    (Join-Path $root 'tools\setup\BbportPortable.cs')
)
& $csc @compileArgs
if ($LASTEXITCODE -ne 0) { throw "Launcher compilation failed: $LASTEXITCODE" }
Move-Item -LiteralPath $compileOutput -Destination $output
Write-Output "Online launcher: $output"
Write-Output "Online payload: $remoteZip"
