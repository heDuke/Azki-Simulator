# SPDX-License-Identifier: GPL-2.0-only
"""Assemble the Windows Engine bundle (AzkiEngine-win-<arch>).

Inputs are local files only (assemble-engine.ps1 downloads and verifies
them): the QEMU/helper build from build-qemu.sh, the CPython embeddable zip
and the pinned wheels. Standard library only, so the embeddable Python that
is being packaged can run this script itself. No firmware is read, bundled or
executed.

Layout (paths the runtime checks are in bundled_runtime.py / bootstrap.py):
  AzkiEngine-win-<arch>/
    Engine/                 manifest.json, bootstrap.py, win32_compat.py,
                            python/ (python.exe, Lib/, Lib/site-packages/),
                            project/ (runner sources, backend/build/*.exe + DLLs),
                            licenses/
    Runtime/                azki-simulator.py + azki_simulator/ (the bridge)
    Firmware/catalog.json   hardware preset metadata only, no firmware
    azki-engine.py, azki-engine.cmd   command-line entry for one session
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / "engine"
RUNTIME = ROOT / "Sources/AzkiSimulator/Runtime"
QEMU_VERSION = "10.1.0"
PYTHON_VERSION = "3.12.10"
PACKAGES = {"pillow": "12.3.0", "littlefs-python": "0.19.0", "psutil": "7.2.2"}
PE_MACHINE = {"x64": 0x8664, "arm64": 0xAA64}
IGNORED = shutil.ignore_patterns("__pycache__", "*.pyc", ".DS_Store")


def pe_machine(path):
    data = path.read_bytes()[:4096]
    offset = int.from_bytes(data[60:64], "little")
    if data[:2] != b"MZ" or data[offset:offset + 4] != b"PE\0\0":
        raise SystemExit(f"{path} is not a PE executable")
    return int.from_bytes(data[offset + 4:offset + 6], "little")


def extract(archive, target, *, skip=()):
    with zipfile.ZipFile(archive) as z:
        for entry in z.infolist():
            path = Path(entry.filename)
            if entry.is_dir() or any(path.match(pattern) for pattern in skip):
                continue
            if path.is_absolute() or ".." in path.parts or ":" in entry.filename:
                raise SystemExit(f"Unsafe archive path in {archive.name}: {entry.filename}")
            destination = target / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(z.read(entry))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arch", choices=sorted(PE_MACHINE), required=True)
    parser.add_argument("--qemu-dir", type=Path, required=True, help="Output directory of build-qemu.ps1")
    parser.add_argument("--python-zip", type=Path, required=True, help="python-3.12.10-embed-<arch>.zip")
    parser.add_argument("--wheel", type=Path, action="append", default=[], help="Pinned cp312 wheel (repeat)")
    parser.add_argument("--out", type=Path, required=True, help="Bundle directory to create")
    parser.add_argument("--force", action="store_true", help="Replace an existing bundle directory")
    args = parser.parse_args()

    out = args.out.absolute()
    if out.exists():
        if not args.force:
            raise SystemExit(f"{out} exists; pass --force to replace it")
        shutil.rmtree(out)
    engine = out / "Engine"
    python = engine / "python"
    python.mkdir(parents=True)

    # CPython embeddable: python.exe/DLLs/.pyd at the top, the standard
    # library (.pyc) extracted from python312.zip into Lib.
    extract(args.python_zip, python)
    stdlib_zip = python / "python312.zip"
    extract(stdlib_zip, python / "Lib")
    stdlib_zip.unlink()
    # sys.path of the bridge process; bootstrap.py sets the runner's itself.
    (python / "python312._pth").write_text("Lib\n.\nLib\\site-packages\n", encoding="ascii")
    site = python / "Lib/site-packages"
    site.mkdir(exist_ok=True)  # python312.zip ships an empty site-packages
    names = sorted(wheel.name.lower() for wheel in args.wheel)
    for package, version in PACKAGES.items():
        stem = package.replace("-", "_") + "-" + version + "-"
        if sum(name.startswith(stem) for name in names) != 1:
            raise SystemExit(f"Need exactly one wheel for {package} {version}")
    for wheel in args.wheel:
        tag = "win_amd64" if args.arch == "x64" else "win_arm64"
        if not wheel.name.endswith(tag + ".whl"):
            raise SystemExit(f"{wheel.name} is not a {tag} wheel")
        extract(wheel, site, skip=("*.data/scripts/*",))

    # Runner sources and the native build.
    shutil.copytree(INPUT / "project", engine / "project", ignore=IGNORED)
    build = engine / "project/backend/build"
    build.mkdir(parents=True)
    expected = {"qemu-system-arm.exe", "yaffs-image.exe", "legacy-ui-db.exe", "instance-props.exe"}
    found = {path.name for path in args.qemu_dir.glob("*.exe")}
    if found != expected:
        raise SystemExit(f"Unexpected QEMU build executables: {sorted(found)}")
    for path in sorted(args.qemu_dir.iterdir()):
        if path.suffix.lower() in (".exe", ".dll"):
            if pe_machine(path) != PE_MACHINE[args.arch]:
                raise SystemExit(f"{path.name} is not a {args.arch} binary")
            shutil.copy2(path, build / path.name)
    shutil.copy2(INPUT / "bootstrap.py", engine / "bootstrap.py")
    shutil.copy2(RUNTIME / "azki_simulator/win32_compat.py", engine / "win32_compat.py")

    licenses = engine / "licenses"
    shutil.copytree(INPUT / "licenses", licenses, ignore=IGNORED)
    shutil.copy2(INPUT / "THIRD_PARTY.md", licenses / "THIRD_PARTY.md")
    shutil.copy2(ROOT / "LICENSE", licenses / "AzkiSimulator-LICENSE")
    for name, source in (("python", python / "LICENSE.txt"),
                         ("yaffs", INPUT / "third-party/yaffs2/LICENSE"),
                         ("unqlite", INPUT / "third-party/unqlite/LICENSE")):
        (licenses / name).mkdir(exist_ok=True)
        shutil.copy2(source, licenses / name / "LICENSE")
    for name in ("BUILD-INFO.txt", "MSYS2-PACKAGES.txt"):
        if (args.qemu_dir / name).is_file():
            shutil.copy2(args.qemu_dir / name, licenses / ("qemu-" + name))

    # The manifest lists every Engine file (bundled_runtime._manifest).
    for path in sorted(engine.rglob("*"), reverse=True):
        if path.is_dir() and not any(path.iterdir()):
            path.rmdir()
    files = []
    for path in sorted(engine.rglob("*")):
        if path.is_file():
            data = path.read_bytes()
            files.append({"path": path.relative_to(engine).as_posix(), "bytes": len(data),
                          "sha256": hashlib.sha256(data).hexdigest(),
                          "executable": path.suffix.lower() == ".exe"})
    capabilities = json.loads((INPUT / "capabilities.json").read_text(encoding="utf-8"))
    qemu = next(item for item in files if item["path"] == "project/backend/build/qemu-system-arm.exe")
    for capability in capabilities.values():
        capability["qemuSHA256"] = qemu["sha256"]
    payload = json.dumps(files, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()
    manifest = {"schema": "azki.simulator.engine.v1", "version": 1, "profile": "local-assets",
                "entrypoints": {"python": "python/python.exe", "qemu": "project/backend/build/qemu-system-arm.exe",
                                "project": "project", "bootstrap": "bootstrap.py"},
                "python": {"version": "3.12", "home": "python", "stdlib": "python/Lib",
                           "sitePackages": "python/Lib/site-packages", "startupFlags": ["-I", "-S", "-B"]},
                "files": files, "payloadSha256": hashlib.sha256(payload).hexdigest(),
                "distribution": {"mode": "runtime-only", "firmwareIncluded": False, "publicReady": False},
                "signing": {"authenticodeSigned": False}, "executionVerified": False,
                "build": {"platform": "windows", "arch": args.arch, "qemu": QEMU_VERSION,
                          "python": PYTHON_VERSION, "packages": PACKAGES},
                **capabilities}
    (engine / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    # The bridge and the CLI entry; the bridge runs on the same Python.
    shutil.copytree(RUNTIME, out / "Runtime", ignore=IGNORED)
    (out / "Firmware").mkdir()
    shutil.copy2(INPUT / "catalog.json", out / "Firmware/catalog.json")
    for name in ("azki-engine.py", "azki-engine.cmd"):
        shutil.copy2(ROOT / "windows/engine" / name, out / name)
    print(json.dumps({"bundle": str(out), "arch": args.arch, "engineFiles": len(files),
                      "qemuSHA256": qemu["sha256"], "firmwareIncluded": False}))


if __name__ == "__main__":
    sys.exit(main())
