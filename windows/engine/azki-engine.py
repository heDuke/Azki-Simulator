# SPDX-License-Identifier: GPL-2.0-only
"""Azki Simulator Engine for Windows: command-line entry (milestone M1).

Runs one simulator session with firmware the user supplies. Nothing is
downloaded and no firmware is bundled. Usage (from the bundle directory):

  azki-engine.cmd selftest
      No firmware: verifies the Engine manifest, QEMU machines, and the
      loopback TCP + token control handshake of the bridge.
  azki-engine.cmd run --firmware D:\\path\\to\\ota.zip [--model MODEL] [--seconds N]
      library-import -> prepare-bundled -> serve -> start, then reports the
      session state and frame.raw updates until Ctrl+C (or N seconds).
  azki-engine.cmd call SESSION status|stop|screenshot|logs [--arguments JSON]
  azki-engine.cmd catalog
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import subprocess
import sys
import tempfile
import time

BUNDLE = Path(os.path.realpath(__file__)).parent
ENGINE = BUNDLE / "Engine"
RUNTIME = BUNDLE / "Runtime"
PYTHON = ENGINE / "python" / "python.exe"
QEMU = ENGINE / "project/backend/build/qemu-system-arm.exe"
CLI = RUNTIME / "azki-simulator.py"
MACHINES = ("best1503", "azki-q65", "azki-p65")


def real(path):
    """Long, absolute path: the runtime rejects 8.3 short names and links."""
    return Path(os.path.realpath(os.path.abspath(path)))


def data_root():
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData/Local")
    return real(Path(base) / "AzkiSimulator")


def check_ascii(label, path):
    # QEMU receives paths through the ANSI code page; keep them ASCII.
    if not str(path).isascii() or "," in str(path):
        raise SystemExit(f"{label} must be an ASCII path without commas: {path}")


def simulator(*arguments, check=True):
    command = [str(PYTHON), "-I", "-S", "-B", "-X", "utf8", str(CLI), *map(str, arguments)]
    completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
    try:
        result = json.loads(completed.stdout)
    except ValueError:
        sys.stderr.write(completed.stdout + completed.stderr)
        raise SystemExit(f"azki-simulator.py {arguments[0]} failed (exit {completed.returncode})")
    if check and result.get("ok") is False:
        print(json.dumps(result, ensure_ascii=False, indent=2))
        raise SystemExit(f"azki-simulator.py {arguments[0]} failed")
    return result


def serve(session, log, *extra):
    command = [str(PYTHON), "-I", "-S", "-B", "-X", "utf8", str(CLI), "serve", str(session), "--no-discovery", *map(str, extra)]
    # Own console group: CTRL_BREAK stops it exactly like Ctrl+C would.
    return subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)


def wait_endpoint(session, process, seconds=30):
    deadline = time.monotonic() + seconds
    while not (session / "control.sock").exists():
        if process.poll() is not None:
            raise SystemExit(f"serve exited early (exit {process.returncode})")
        if time.monotonic() > deadline:
            raise SystemExit("serve did not publish control.sock")
        time.sleep(.1)


def stop_serve(process, seconds=30):
    """True when serve stopped gracefully (CTRL_BREAK -> its shutdown path)."""
    if process.poll() is not None:
        return True
    try:
        process.send_signal(signal.CTRL_BREAK_EVENT)
    except OSError as error:  # No shared console (e.g. a detached parent).
        print("CTRL_BREAK unavailable:", error, file=sys.stderr)
        process.kill()
        process.wait()
        return False
    try:
        process.wait(timeout=seconds)
        return True
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        return False


def selftest(args):
    report = {}
    sys.path.insert(0, str(RUNTIME))
    from azki_simulator.bundled_runtime import _manifest
    info = _manifest(ENGINE, verify=True)  # Every listed file, size and SHA-256.
    report["engineFiles"] = len(info["files"])
    readiness = simulator("library-catalog", "--engine-root", ENGINE,
                          "--library-root", real(tempfile.mkdtemp(prefix="azki-selftest-library-")))
    if readiness["runtime"].get("ready") is not True:
        raise SystemExit("Engine is not ready: " + json.dumps(readiness["runtime"], ensure_ascii=False))
    report["runtimeReady"] = True
    version = subprocess.run([str(QEMU), "--version"], capture_output=True, text=True, check=True).stdout
    machines = subprocess.run([str(QEMU), "-machine", "help"], capture_output=True, text=True, check=True).stdout
    missing = [name for name in MACHINES if not any(line.split()[:1] == [name] for line in machines.splitlines())]
    if missing:
        raise SystemExit("QEMU lacks machines: " + ", ".join(missing))
    report["qemu"] = version.splitlines()[0]
    report["machines"] = list(MACHINES)

    # Control handshake without firmware: a legacy session over placeholder
    # files is only fingerprinted, never executed.
    work = real(tempfile.mkdtemp(prefix="azki-selftest-"))
    source = work / "source"
    for name in ("build/qemu-system-arm", "firmware/vela_ap.bin", "firmware/vela_resource.bin"):
        (source / name).parent.mkdir(parents=True, exist_ok=True)
        (source / name).write_bytes(secrets.token_bytes(1024))
    session = work / "session"
    simulator("prepare", session, "--source", source)
    with open(work / "serve.log", "wb") as log:
        process = serve(session, log)
        try:
            wait_endpoint(session, process)
            endpoint = json.loads((session / "control.sock").read_text(encoding="utf-8"))
            if endpoint.get("host") != "127.0.0.1" or len(endpoint.get("token") or "") != 64:
                raise SystemExit("Unexpected control endpoint record")
            status = simulator("call", session, "status")
            if status.get("ok") is not True or status["result"].get("state") != "stopped":
                raise SystemExit("Unexpected status reply: " + json.dumps(status))
            # A client without the token is closed without any reply.
            with socket.create_connection(("127.0.0.1", endpoint["port"]), timeout=5) as rogue:
                rogue.sendall(b"0" * 64 + b"\n" + b'{"version":1,"command":"status"}\n')
                try:
                    reply = rogue.recv(4096)
                except (ConnectionResetError, ConnectionAbortedError):
                    reply = b""
            if reply:
                raise SystemExit("Bridge answered a client without the session token")
        finally:
            graceful = stop_serve(process)
    if graceful and (session / "control.sock").exists():
        raise SystemExit("control.sock remained after serve stopped")
    if not graceful and args.require_graceful_stop:
        raise SystemExit("serve did not stop gracefully on CTRL_BREAK")
    report.update(controlHandshake="tcp+token", tokenRejected=True, gracefulStop=graceful,
                  serveExit=process.returncode, firmwareUsed=False)
    report["processGroup"] = process_group_check()
    print(json.dumps({"ok": True, **report}, indent=2))


GROUP_LEADER = r"""
import os, subprocess, sys, time
sys.path.insert(0, sys.argv[1])
from azki_simulator import win32_compat
win32_compat.install()
os.setsid()
child = subprocess.Popen([sys.executable, "-I", "-S", "-c", "import time; time.sleep(120)"])
print(child.pid, flush=True)
time.sleep(120)
"""


def process_group_check():
    """Job-Object process groups as the supervisor uses them (no firmware)."""
    from azki_simulator import win32_compat  # Installed by the import above.
    leader = subprocess.Popen([str(PYTHON), "-I", "-S", "-B", "-c", GROUP_LEADER, str(RUNTIME)],
                              stdout=subprocess.PIPE, text=True)
    try:
        grandchild = int(leader.stdout.readline())
        members = sorted(win32_compat.group_members(leader.pid))
        if members != sorted((leader.pid, grandchild)):
            raise SystemExit(f"Unexpected job members {members}")
        if os.getpgid(grandchild) != leader.pid:
            raise SystemExit("getpgid does not report the job leader")
        os.killpg(leader.pid, signal.SIGTERM)
        leader.wait(timeout=10)
        deadline = time.monotonic() + 10
        while win32_compat.group_members(leader.pid) and time.monotonic() < deadline:
            time.sleep(.1)
        try:
            os.kill(grandchild, 0)
            raise SystemExit("A group member survived killpg")
        except ProcessLookupError:
            pass
    finally:
        if leader.poll() is None:
            leader.kill()
    return {"members": 2, "getpgid": True, "killpg": True}


def run(args):
    firmware = real(args.firmware)
    library = real(args.library or data_root() / "Library")
    sessions = real(args.sessions or data_root() / "Sessions")
    for label, path in (("Bundle", BUNDLE), ("Firmware", firmware), ("Library", library), ("Sessions", sessions)):
        check_ascii(label, path)
    if not firmware.is_file():
        raise SystemExit(f"Firmware file not found: {firmware}")
    library.parent.mkdir(parents=True, exist_ok=True)
    sessions.mkdir(parents=True, exist_ok=True)
    profile = args.profile_id
    if profile is None:
        imported = simulator("library-import", "--engine-root", ENGINE, "--library-root", library,
                             "--input", firmware, *(["--model", args.model] if args.model else []))
        candidates = [item["id"] for item in imported.get("items", [])] or imported.get("candidateIDs", [])
        print("Imported", imported["sha256"], "candidates:", ", ".join(candidates) or "-")
        if len(candidates) != 1:
            raise SystemExit("Choose the device with --model MODEL (or --profile-id ID): " + ", ".join(candidates))
        profile = candidates[0]
    root = sessions / (time.strftime("s-%Y%m%d-%H%M%S-") + secrets.token_hex(3))
    prepared = simulator("prepare-bundled", "--engine-root", ENGINE, "--library-root", library,
                         "--profile-id", profile, "--session-root", root)
    session = root / "session"
    print("Profile:", profile)
    print("Session:", session)
    log_path = root / "serve.log"
    with open(log_path, "wb") as log:
        process = serve(session, log, "--approved-identity-sha256", prepared["identityDigest"])
        try:
            wait_endpoint(session, process)
            started = simulator("call", session, "start", check=False)
            print("start:", json.dumps(started.get("result", started.get("error")), ensure_ascii=False))
            deadline = time.monotonic() + args.seconds if args.seconds else None
            last = None
            while deadline is None or time.monotonic() < deadline:
                time.sleep(2)
                status = simulator("call", session, "status", check=False).get("result", {})
                frame = session / "frame.raw"
                frame_state = (frame.stat().st_mtime_ns, frame.stat().st_size) if frame.exists() else None
                line = (status.get("state"), status.get("adapterState"), status.get("error"), frame_state)
                if line != last:
                    print(time.strftime("%H:%M:%S"), "state=%s adapter=%s error=%s" % line[:3],
                          "frame.raw=" + ("%d bytes, updated" % frame_state[1] if frame_state else "absent"),
                          flush=True)
                    last = line
                if status.get("state") in ("stopped", "crashed") and status.get("lastExit") is not None:
                    print(json.dumps(status, ensure_ascii=False, indent=2))
                    break
        except KeyboardInterrupt:
            print("Stopping…")
        finally:
            if process.poll() is None:
                stop = simulator("call", session, "stop", check=False)
                print("stop:", json.dumps(stop.get("result", stop.get("error")), ensure_ascii=False)[:400])
            stop_serve(process)
    print("Logs: serve", log_path, "| runs", ", ".join(str(p) for p in sorted(session.glob("m-*"))))


def call(args):
    print(json.dumps(simulator("call", real(args.session), args.command, "--arguments", args.arguments, check=False),
                     ensure_ascii=False, indent=2))


def catalog(args):
    library = real(args.library or data_root() / "Library")
    library.parent.mkdir(parents=True, exist_ok=True)
    print(json.dumps(simulator("library-catalog", "--engine-root", ENGINE, "--library-root", library),
                     ensure_ascii=False, indent=2))


def main():
    if sys.platform != "win32":
        raise SystemExit("azki-engine.py is the Windows Engine entry")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="action", required=True)
    test = commands.add_parser("selftest", help="No firmware: Engine, QEMU and control handshake checks")
    test.add_argument("--require-graceful-stop", action="store_true", help="Fail unless CTRL_BREAK stops serve")
    runner = commands.add_parser("run", help="Import firmware, prepare one session and run it")
    runner.add_argument("--firmware", type=Path, required=True, help="Your OTA package (.zip) or AP image")
    runner.add_argument("--model", help="Device model to bind, e.g. miwear.watch.n66cn, when the OTA fits several")
    runner.add_argument("--profile-id", help="Use an already imported profile ID instead of importing")
    runner.add_argument("--seconds", type=int, default=0, help="Stop after N seconds (default: until Ctrl+C)")
    runner.add_argument("--library", type=Path, help=r"Default: %%LOCALAPPDATA%%\AzkiSimulator\Library")
    runner.add_argument("--sessions", type=Path, help=r"Default: %%LOCALAPPDATA%%\AzkiSimulator\Sessions")
    caller = commands.add_parser("call", help="One request to a served session")
    caller.add_argument("session", type=Path)
    caller.add_argument("command")
    caller.add_argument("--arguments", default="{}")
    listing = commands.add_parser("catalog", help="Imported firmware and hardware presets")
    listing.add_argument("--library", type=Path)
    args = parser.parse_args()
    {"selftest": selftest, "run": run, "call": call, "catalog": catalog}[args.action](args)


if __name__ == "__main__":
    main()
