#!/usr/bin/env pwsh
# Builds the questie app and wraps it into a single-file launcher (dist/questie[.exe]).
# The launcher embeds the whole app and unpacks it on first run / version change.
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Rid
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

function Resolve-HostRid {
    if ($env:OS -eq "Windows_NT") { return "win-x64" }
    $uname = ""
    try { $uname = (& uname -srm 2>$null) -join " " } catch { }
    if ($uname -match "Linux.*aarch64") { return "linux-arm64" }
    if ($uname -match "Linux") { return "linux-x64" }
    if ($uname -match "Darwin.*arm64") { return "osx-arm64" }
    return "linux-x64"
}

if (-not $Rid) { $Rid = Resolve-HostRid }

# the app version is the single source of truth, taken from Program.cs
$program = Get-Content (Join-Path $root "Program.cs") -Raw
$match = [regex]::Match($program, 'const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw "could not read the app version from Program.cs" }
$version = $match.Groups[1].Value

Write-Host "==> questie $version  ($Configuration / $Rid)"

# on linux/macos Qt Bridge needs an external Qt install, pointed at by QTDIR
$qtArgs = @()
if ($env:QTDIR) { $qtArgs += "-p:QtDir=$env:QTDIR" }

# 1. build the app (Qt Bridge deploys Qt into the same output directory)
dotnet build (Join-Path $root "questie.csproj") -c $Configuration @qtArgs
if ($LASTEXITCODE -ne 0) { throw "app build failed" }

$appOut = Join-Path $root "bin\$Configuration\net10.0"
if (-not (Test-Path $appOut)) { throw "app output not found: $appOut" }

# 2. stage the payload, dropping build-only files
$stage = Join-Path $root "obj\launcher-payload"
$payload = Join-Path $root "obj\payload.zip"
$launcherOut = Join-Path $root "obj\launcher-out"
Remove-Item $stage, $launcherOut -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $payload -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $appOut "*") $stage -Recurse -Force
Get-ChildItem $stage -Recurse -File |
    Where-Object { $_.Extension -eq ".pdb" -or $_.Name -eq "qtdeploy.txt" } |
    Remove-Item -Force

# 3. zip the payload
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("    payload: {0:N1} MB" -f ((Get-Item $payload).Length / 1MB))

# 4. build the launcher (embeds the payload and produces a single file)
dotnet publish (Join-Path $root "launcher\QuestieLauncher.csproj") `
    -c $Configuration -r $Rid --self-contained false -o $launcherOut `
    "-p:AppVersion=$version" "-p:PayloadZip=$payload"
if ($LASTEXITCODE -ne 0) { throw "launcher build failed" }

# 5. publish the single-file launcher
$exeName = if ($Rid -like "win-*") { "questie.exe" } else { "questie" }
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Copy-Item (Join-Path $launcherOut $exeName) (Join-Path $distDir $exeName) -Force

$size = (Get-Item (Join-Path $distDir $exeName)).Length / 1MB
Write-Host ("==> wrote dist\{0} ({1:N1} MB), app version {2}" -f $exeName, $size, $version)
