<#
.SYNOPSIS
  Build qemu-system-arm.exe (QEMU 10.1.0 + engine/qemu-overlay) and the engine
  C helpers with MSYS2, for win-x64 (UCRT64) or win-arm64 (CLANGARM64).

.EXAMPLE
  pwsh windows/scripts/build-qemu.ps1 -Arch x64 -InstallDependencies
  pwsh windows/scripts/build-qemu.ps1 -Arch arm64 -Msys2Root C:\msys64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }),
    [string]$Msys2Root = 'C:\msys64',
    [string]$WorkDir,
    [string]$OutDir,
    [int]$Jobs = [Environment]::ProcessorCount,
    # Install the MSYS2 packages this build needs with pacman first.
    [switch]$InstallDependencies
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $WorkDir) { $WorkDir = Join-Path $repo "build\windows-engine\work" }
if (-not $OutDir) { $OutDir = Join-Path $repo "build\windows-engine\qemu-win-$Arch" }
$msystem = if ($Arch -eq 'arm64') { 'CLANGARM64' } else { 'UCRT64' }
$packagePrefix = if ($Arch -eq 'arm64') { 'mingw-w64-clang-aarch64' } else { 'mingw-w64-ucrt-x86_64' }

$envExe = Join-Path $Msys2Root 'usr\bin\env.exe'
if (-not (Test-Path $envExe)) {
    throw "MSYS2 was not found at $Msys2Root. Install it from https://www.msys2.org/ or pass -Msys2Root."
}

function Invoke-Msys2([string]$command) {
    & $envExe "MSYSTEM=$msystem" 'CHERE_INVOKING=1' 'MSYS2_PATH_TYPE=minimal' /usr/bin/bash -lc $command
    if ($LASTEXITCODE -ne 0) { throw "MSYS2 command failed ($LASTEXITCODE): $command" }
}

if ($InstallDependencies) {
    $packages = @('cc', 'glib2', 'pixman', 'dtc', 'pkgconf', 'meson', 'ninja', 'python', 'zstd') |
        ForEach-Object { "$packagePrefix-$_" }
    $packages += @('make', 'diffutils', 'tar', 'xz', 'curl')
    if ($Arch -eq 'arm64') { $packages += 'binutils' }  # windres/objdump for LLVM toolchains
    Invoke-Msys2 ("pacman -S --needed --noconfirm " + ($packages -join ' '))
}

New-Item -ItemType Directory -Force -Path $WorkDir, (Split-Path $OutDir) | Out-Null
$env:AZKI_REPO = $repo
$env:AZKI_WORK = $WorkDir
$env:AZKI_OUT = $OutDir
$env:AZKI_JOBS = "$Jobs"
Push-Location $repo
try {
    Invoke-Msys2 'set -e; r="$(cygpath -u "$AZKI_REPO")"; "$r/windows/scripts/build-qemu.sh" "$(cygpath -u "$AZKI_WORK")" "$(cygpath -u "$AZKI_OUT")" "$AZKI_JOBS"'
} finally {
    Pop-Location
}
Write-Host "QEMU for win-$Arch is in $OutDir"
