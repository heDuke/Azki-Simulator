<#
.SYNOPSIS
  Assemble the Windows Engine bundle AzkiEngine-win-<arch> from the
  build-qemu.ps1 output, CPython 3.12.10 embeddable and pinned wheels
  (pillow, littlefs-python, psutil). Downloads are verified by SHA-256.
  No firmware is downloaded or bundled.

.EXAMPLE
  pwsh windows/scripts/assemble-engine.ps1 -Arch x64
  pwsh windows/scripts/assemble-engine.ps1 -Arch arm64 -Zip
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }),
    [string]$QemuDir,
    [string]$CacheDir,
    [string]$OutDir,
    # Also write AzkiEngine-win-<arch>.zip next to the bundle directory.
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $QemuDir) { $QemuDir = Join-Path $repo "build\windows-engine\qemu-win-$Arch" }
if (-not $CacheDir) { $CacheDir = Join-Path $repo 'build\windows-engine\downloads' }
if (-not $OutDir) { $OutDir = Join-Path $repo "build\windows-engine\AzkiEngine-win-$Arch" }
if (-not (Test-Path (Join-Path $QemuDir 'qemu-system-arm.exe'))) {
    throw "QEMU build not found in $QemuDir; run build-qemu.ps1 -Arch $Arch first."
}

$py = if ($Arch -eq 'arm64') { 'arm64' } else { 'amd64' }
$whl = if ($Arch -eq 'arm64') { 'win_arm64' } else { 'win_amd64' }
$files = 'https://files.pythonhosted.org/packages'
$inputs = @{
    x64 = @(
        @{ Name = 'python-3.12.10-embed-amd64.zip'; Url = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip'
           Sha256 = '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3' }
        @{ Name = 'pillow-12.3.0-cp312-cp312-win_amd64.whl'; Url = "$files/45/89/da2f7971a317f83d807fdd4065c0af40208e59e692cc43d315a71a0e96d1/pillow-12.3.0-cp312-cp312-win_amd64.whl"
           Sha256 = 'a2b55dd6b2a4c4b7d87ffa56bdb33fdc5fdb9a462173861a7bc097f17d91cb09' }
        @{ Name = 'littlefs_python-0.19.0-cp312-cp312-win_amd64.whl'; Url = "$files/f0/52/66fab84b0168d9fdf652e5cc3978ac816ec533fbfc742d974920efe9c2c9/littlefs_python-0.19.0-cp312-cp312-win_amd64.whl"
           Sha256 = '0aaae1347d5a05da5e582b0d4c241d70b2736d01e5dbc58f9ae39ac659fc8393' }
        @{ Name = 'psutil-7.2.2-cp37-abi3-win_amd64.whl'; Url = "$files/b4/90/e2159492b5426be0c1fef7acba807a03511f97c5f86b3caeda6ad92351a7/psutil-7.2.2-cp37-abi3-win_amd64.whl"
           Sha256 = 'eb7e81434c8d223ec4a219b5fc1c47d0417b12be7ea866e24fb5ad6e84b3d988' }
    )
    arm64 = @(
        @{ Name = 'python-3.12.10-embed-arm64.zip'; Url = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-arm64.zip'
           Sha256 = '3065efc3d382d1cda66757ac71ade11904fa6e350f5a97eb74811acd71ba5532' }
        @{ Name = 'pillow-12.3.0-cp312-cp312-win_arm64.whl'; Url = "$files/de/47/4845a0a6c0dbf1db8456bd9fc791f13c5ced7ced20606d08a0aacfd25b49/pillow-12.3.0-cp312-cp312-win_arm64.whl"
           Sha256 = '331b624368d4f1d069149002f25f44bc61c8919ce8ddb3c45bdad8f6e2d89510' }
        @{ Name = 'littlefs_python-0.19.0-cp312-cp312-win_arm64.whl'; Url = "$files/d0/07/e3c3296af085547b3fb4c6ad25bf330bd9c17a2805ccb33a14f2b0b9f7c6/littlefs_python-0.19.0-cp312-cp312-win_arm64.whl"
           Sha256 = 'c170794e550c18853f2ea1767df526aa879c27c963064cd88920994430cc6ffb' }
        @{ Name = 'psutil-7.2.2-cp37-abi3-win_arm64.whl'; Url = "$files/8c/c7/7bb2e321574b10df20cbde462a94e2b71d05f9bbda251ef27d104668306a/psutil-7.2.2-cp37-abi3-win_arm64.whl"
           Sha256 = '8c233660f575a5a89e6d4cb65d9f938126312bca76d8fe087b947b3a1aaac9ee' }
    )
}

New-Item -ItemType Directory -Force $CacheDir | Out-Null
$paths = @{}
foreach ($item in $inputs[$Arch]) {
    $target = Join-Path $CacheDir $item.Name
    if (-not (Test-Path $target) -or (Get-FileHash -Algorithm SHA256 $target).Hash -ne $item.Sha256.ToUpperInvariant()) {
        Write-Host "Downloading $($item.Name)"
        Invoke-WebRequest -Uri $item.Url -OutFile "$target.part" -UseBasicParsing
        Move-Item -Force "$target.part" $target
    }
    $hash = (Get-FileHash -Algorithm SHA256 $target).Hash
    if ($hash -ne $item.Sha256.ToUpperInvariant()) { throw "SHA-256 mismatch for $($item.Name): $hash" }
    $paths[$item.Name] = $target
}

# Run the assembler with the embeddable Python being packaged (stdlib only);
# on an x64 host assembling arm64 (or the reverse) any 3.12 python works.
$embed = $paths["python-3.12.10-embed-$py.zip"]
$hostPython = Join-Path $CacheDir "host-python-$py"
if (-not (Test-Path (Join-Path $hostPython 'python.exe'))) {
    Expand-Archive -Force $embed $hostPython
}
$python = Join-Path $hostPython 'python.exe'
& $python -c "import sys" 2>$null
if ($LASTEXITCODE -ne 0) {
    $python = (Get-Command python -ErrorAction Stop).Source  # Cross-arch host without emulation
}
$wheels = $paths.Keys | Where-Object { $_ -like "*.whl" } | ForEach-Object { '--wheel'; $paths[$_] }
& $python -I -S -B (Join-Path $PSScriptRoot 'assemble-engine.py') --arch $Arch --qemu-dir $QemuDir `
    --python-zip $embed @wheels --out $OutDir --force
if ($LASTEXITCODE -ne 0) { throw "assemble-engine.py failed ($LASTEXITCODE)" }

if ($Zip) {
    $archive = "$OutDir.zip"
    Remove-Item -Force -ErrorAction SilentlyContinue $archive
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $archive
    Write-Host "Wrote $archive"
}
Write-Host "Engine bundle: $OutDir"
