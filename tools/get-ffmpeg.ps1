# Puts ffmpeg.exe into tools/ffmpeg, where the build copies it next to Lupik (ffmpeg\ffmpeg.exe).
# Called by the build when the file is missing: the LGPL build of the newest FFmpeg release branch from
# BtbN/FFmpeg-Builds, checked against that release's SHA-256 list. Not kept in git (it's ~130 MB).
param([Parameter(Mandatory = $true)][string]$Destination)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower
$release = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/'

$checksums = (Invoke-WebRequest -UseBasicParsing ($release + 'checksums.sha256')).Content
if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::UTF8.GetString($checksums) }
$builds = [regex]::Matches($checksums, '(?m)^([0-9a-f]{64})\s+(ffmpeg-n(\d+)\.(\d+)-latest-win64-lgpl-\3\.\4\.zip)\s*$') |
    Sort-Object { [int]$_.Groups[3].Value }, { [int]$_.Groups[4].Value } -Descending
if (-not $builds) { throw 'No FFmpeg LGPL build in the release.' }
$hash = $builds[0].Groups[1].Value
$name = $builds[0].Groups[2].Value
Write-Host "Downloading $name"

New-Item -ItemType Directory -Force $Destination | Out-Null
$zip = Join-Path $Destination 'download.zip.part'
try {
    Invoke-WebRequest -UseBasicParsing ($release + $name) -OutFile $zip
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $hash) { throw "Checksum mismatch for $name" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -like '*/bin/ffmpeg.exe' } | Select-Object -First 1
        if (-not $entry) { throw 'ffmpeg.exe is missing from the download.' }
        $target = Join-Path $Destination 'ffmpeg.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$target.part", $true)
        Move-Item -Force "$target.part" $target
        # Its license travels with it
        $license = $archive.Entries | Where-Object { $_.FullName -like '*/LICENSE.txt' } | Select-Object -First 1
        $licenseFile = Join-Path $Destination 'LICENSE.txt'
        if ($license) { [IO.Compression.ZipFileExtensions]::ExtractToFile($license, $licenseFile, $true) }
        else { Set-Content -Encoding utf8 $licenseFile "FFmpeg ($name) is licensed under the GNU LGPL 2.1 or later: https://ffmpeg.org/legal.html" }
    }
    finally { $archive.Dispose() }
    Write-Host "FFmpeg ready: $target"
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $zip
}
