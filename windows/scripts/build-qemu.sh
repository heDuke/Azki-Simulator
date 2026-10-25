#!/usr/bin/env bash
# Build qemu-system-arm.exe (QEMU 10.1.0 + engine/qemu-overlay, arm-softmmu
# only) and the three engine C helpers inside an MSYS2 shell, then collect the
# MSYS2 runtime DLLs they need.
#
# Run from an MSYS2 UCRT64 (win-x64) or CLANGARM64 (win-arm64) shell, normally
# through build-qemu.ps1:
#   windows/scripts/build-qemu.sh <work-dir> <output-dir> [jobs]
# Mirrors the configure flags of scripts/build-engine.py. No firmware is used.
set -euo pipefail

QEMU_VERSION=10.1.0
QEMU_SHA256=e0517349b50ca73ebec2fa85b06050d5c463ca65c738833bd8fc1f15f180be51

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="${1:?work directory}"
out="${2:?output directory}"
jobs="${3:-$(nproc)}"

case "${MSYSTEM:-}" in
  UCRT64) expected_machine=8664 ;;      # IMAGE_FILE_MACHINE_AMD64
  CLANGARM64) expected_machine=aa64 ;;  # IMAGE_FILE_MACHINE_ARM64
  *) echo "Run inside an MSYS2 UCRT64 or CLANGARM64 shell (MSYSTEM=${MSYSTEM:-unset})" >&2; exit 2 ;;
esac
prefix="${MINGW_PREFIX:?MINGW_PREFIX is not set}"
cc="${CC:-cc}"

mkdir -p "$work" "$out"
work="$(cd "$work" && pwd)"
out="$(cd "$out" && pwd)"
archive="$work/qemu-$QEMU_VERSION.tar.xz"
if [ ! -f "$archive" ]; then
  curl --fail --location --retry 5 --retry-delay 5 -o "$archive.part" \
    "https://download.qemu.org/qemu-$QEMU_VERSION.tar.xz"
  mv "$archive.part" "$archive"
fi
echo "$QEMU_SHA256  $archive" | sha256sum -c -

source_dir="$work/qemu-$QEMU_VERSION"
if [ ! -f "$source_dir/.azki-extracted" ]; then
  rm -rf "$source_dir"
  # The tarball contains symbolic links that MSYS2 tar cannot always create
  # on NTFS; none of them are used by an arm-softmmu build.
  tar -xf "$archive" -C "$work" || true
  test -f "$source_dir/configure"
  touch "$source_dir/.azki-extracted"
fi
# Like build-engine.py: copy the overlay over the pristine tree every build.
cp -R "$root/engine/qemu-overlay/." "$source_dir/"

build_dir="$work/qemu-build-$MSYSTEM"
mkdir -p "$build_dir"
flags=(--target-list=arm-softmmu --with-devices-arm=mi-emu --without-default-devices
       --disable-docs --disable-tools --disable-guest-agent --disable-gtk --disable-sdl --disable-vnc
       --disable-opengl --disable-werror --disable-download --audio-drv-list= --disable-slirp
       --python="$prefix/bin/python3")
stamp="$build_dir/.azki-configure"
if [ ! -f "$build_dir/build.ninja" ] || [ "$(cat "$stamp" 2>/dev/null)" != "${flags[*]}" ]; then
  (cd "$build_dir" && "$source_dir/configure" "${flags[@]}")
  printf '%s' "${flags[*]}" > "$stamp"
fi
ninja -C "$build_dir" -j "$jobs" qemu-system-arm.exe

tools="$work/tools-$MSYSTEM"
mkdir -p "$tools"
y="$root/engine/third-party/yaffs2"
u="$root/engine/third-party/unqlite"
src="$root/engine/project/backend/src"
yaffs_defs=(-DCONFIG_YAFFS_DIRECT -DCONFIG_YAFFS_YAFFS2 -DCONFIG_YAFFS_DEFINES_TYPES -DCONFIG_YAFFS_PROVIDE_DEFS
            -DCONFIG_YAFFSFS_PROVIDE_VALUES -DCONFIG_YAFFS_USE_32_BIT_TIME_T -Dloff_t=int64_t
            -include stdint.h -include sys/types.h)
yaffs_direct=("$y/direct/yaffsfs.c" "$y/direct/yaffs_attribs.c" "$y/direct/yaffs_error.c" "$y/direct/yaffs_hweight.c")
"$cc" -O2 "${yaffs_defs[@]}" -I"$y/core" -I"$y/direct" "$src/yaffs_image.c" "$y"/core/*.c "${yaffs_direct[@]}" \
  -o "$tools/yaffs-image.exe"
"$cc" -O2 -I"$u" "$src/legacy_ui_db.c" "$u/unqlite.c" -o "$tools/legacy-ui-db.exe"
"$cc" -O2 "${yaffs_defs[@]}" -I"$y/core" -I"$y/direct" -I"$u" "$src/instance_props.c" "$y"/core/*.c \
  "${yaffs_direct[@]}" "$u/unqlite.c" -o "$tools/instance-props.exe"

rm -rf "$out"
mkdir -p "$out"
cp "$build_dir/qemu-system-arm.exe" "$tools"/*.exe "$out/"
# Copy every DLL from this MSYS2 prefix that the executables import,
# transitively. System DLLs (outside the prefix) are left to Windows.
declare -A seen=()
queue=("$out"/*.exe)
while [ "${#queue[@]}" -gt 0 ]; do
  item="${queue[0]}"; queue=("${queue[@]:1}")
  while read -r dll; do
    [ -n "$dll" ] || continue
    key="${dll,,}"
    [ -z "${seen[$key]:-}" ] || continue
    seen[$key]=1
    if [ -f "$prefix/bin/$dll" ]; then
      cp "$prefix/bin/$dll" "$out/"
      queue+=("$out/$dll")
    fi
  done < <(objdump -p "$item" | sed -n 's/^\s*DLL Name: //p' | tr -d '\r')
done

for exe in "$out"/*.exe "$out"/*.dll; do
  machine="$(objdump -f "$exe" | sed -n 's/.*architecture: \([^,]*\).*/\1/p')"
  echo "$(basename "$exe"): $machine"
done
# PE machine check of the main executable (offset of "PE\0\0" + 4).
pe="$(od -An -tu4 -j60 -N4 "$out/qemu-system-arm.exe" | tr -d ' ')"
machine="$(od -An -tx2 -j$((pe + 4)) -N2 "$out/qemu-system-arm.exe" | tr -d ' ')"
if [ "$machine" != "$expected_machine" ]; then
  echo "qemu-system-arm.exe has PE machine $machine, expected $expected_machine" >&2
  exit 1
fi
"$out/qemu-system-arm.exe" --version
"$out/qemu-system-arm.exe" -machine help
{
  echo "qemu=$QEMU_VERSION"
  echo "qemuSHA256=$QEMU_SHA256"
  echo "msystem=$MSYSTEM"
  echo "configure=${flags[*]}"
  echo "cc=$("$cc" --version | head -1)"
} > "$out/BUILD-INFO.txt"
pacman -Q > "$out/MSYS2-PACKAGES.txt" 2>/dev/null || true
echo "Built QEMU and helpers into $out"
