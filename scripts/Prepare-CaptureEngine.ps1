$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'App/capture-engine'
$archive = Join-Path $work 'ffmpeg-n9.0.2-36-g6882969ce9-win64-lgpl-shared-9.0.zip'
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-26-14-25/ffmpeg-n9.0.2-36-g6882969ce9-win64-lgpl-shared-9.0.zip'
$sha = '6be6e82a5cdc08aa173cf0a7b746682b4f16492d79473dc09d7c41be834e507b'
New-Item -ItemType Directory -Path $work -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) { throw 'Capture engine archive failed SHA-256 verification.' }
$unpacked = Join-Path $work 'unpacked'
if (-not (Test-Path -LiteralPath $unpacked)) { Expand-Archive -LiteralPath $archive -DestinationPath $unpacked }
$source = Get-ChildItem -LiteralPath $unpacked -Directory | Select-Object -First 1
$bundle = Join-Path $work 'bundle'
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $source.FullName 'bin') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in 'ffmpeg.exe','ffprobe.exe' } | Copy-Item -Destination $bundle
Copy-Item -LiteralPath (Join-Path $source.FullName 'LICENSE.txt') -Destination $bundle
$manifest = [ordered]@{}
Get-ChildItem -LiteralPath $bundle -File | Where-Object Name -ne 'manifest.json' | ForEach-Object { $manifest[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'manifest.json') -Encoding utf8
$destination = Join-Path $root 'Rig/Assets/capture-engine.zip'
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $destination -Force -CompressionLevel Optimal
Write-Output "Prepared pinned LGPL capture engine: $destination"
