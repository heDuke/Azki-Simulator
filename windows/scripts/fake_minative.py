# SPDX-License-Identifier: GPL-2.0-only
"""A synthetic multi-device session whose runner is a stand-in (no firmware, no QEMU).

``prepare`` writes a real minative session (session.json + backend-plan.json,
via create_minative_session) whose plan launches FAKE_RUNNER in place of
bootstrap.py/multi_device.py. The fake runner behaves like the real one at
every boundary the host checks:

* QMP and input endpoints in ``<run>/tmp/q66-qmp-*/`` (loopback TCP endpoint
  files with a token on Windows, Unix sockets elsewhere);
* a "core" child process (the QEMU stand-in) in the runner's process group,
  writing ``lcd-frame.raw`` in place at 60 Hz (odd sequence while writing)
  and appending to ``qemu.log``;
* identity.json/board.json, then control.json naming that core PID;
* touch input acknowledged with ``{"return": {}}``, recorded in fake-input.log;
* CTRL_BREAK (Windows) / SIGINT stops it with result.json outcome
  "interrupted", like the runner's normal stop.

Used by test_minative_supervision.py and by Azki.Core's engine integration
test (``python fake_minative.py RUNTIME_DIR WORK_DIR`` prints the session).
"""
import json
import os
from pathlib import Path
import sys

WIDTH, HEIGHT = 336, 480
PROFILE = "fake.p67"
PIXEL = 0x07E0  # RGB565 pure green: BGRA FF00FC00

FAKE_RUNNER = r'''
import json, os, signal, socket, socketserver, struct, subprocess, sys, tempfile, threading, time
from pathlib import Path
RUNTIME = %(runtime)r
arguments = sys.argv[1:]
run = Path(arguments[arguments.index("--log-dir") + 1])
profile = arguments[arguments.index("--device") + 1]
windows = sys.platform == "win32"
if windows:
    sys.path.insert(0, RUNTIME)
    from azki_simulator import win32_compat
    win32_compat.install()
stopping = threading.Event()
for name in ("SIGBREAK" if windows else "SIGINT", "SIGTERM"):
    signal.signal(getattr(signal, name), lambda *_: stopping.set())

temporary = Path(tempfile.mkdtemp(prefix="q66-qmp-"))
qmp_path, input_path = temporary / "qmp.sock", temporary / "input.sock"
if windows:
    qmp = win32_compat.listen(qmp_path, token=False)  # QEMU's QMP has no token
else:
    qmp = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    qmp.bind(str(qmp_path))
    qmp.listen(1)

class Input(socketserver.StreamRequestHandler):
    def handle(self):
        self.connection.settimeout(2)
        event = json.loads(self.rfile.readline(257))
        with open(run / "fake-input.log", "a") as log:
            log.write(json.dumps(event, sort_keys=True) + "\n")
        self.wfile.write(b'{"return": {}}\n')

server = (win32_compat.stream_server(input_path, Input) if windows
          else socketserver.ThreadingUnixStreamServer(str(input_path), Input))
server.daemon_threads = True
threading.Thread(target=server.serve_forever, daemon=True).start()

frame = run / "lcd-frame.raw"
core = subprocess.Popen([sys.executable, "-I", "-S", "-B", "-c", %(core)r, str(frame), str(run / "qemu.log"),
                         str(%(width)d), str(%(height)d), str(%(pixel)d)],
                        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if windows else 0)
deadline = time.monotonic() + 30
while not frame.exists() and core.poll() is None and time.monotonic() < deadline:
    time.sleep(.01)
(run / "identity.json").write_text(json.dumps({"profile_id": profile}))
(run / "board.json").write_text(json.dumps({"profile_id": profile}))
(run / "control.json").write_text(json.dumps({"qmp": str(qmp_path), "framebuffer": str(frame), "pid": core.pid,
                                              "input_socket": str(input_path)}, indent=2) + "\n")
print("fake runner: control published", flush=True)
while not stopping.is_set() and core.poll() is None:
    time.sleep(.05)
core.terminate()
core.wait(10)
server.shutdown()
(run / "control.json").unlink(missing_ok=True)
(run / "result.json").write_text(json.dumps({"outcome": "interrupted"}))
print("fake runner: stopped", flush=True)
'''

FAKE_CORE = r'''
import os, signal, struct, sys, time
frame, log, width, height, pixel = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
if sys.platform == "win32":
    signal.signal(signal.SIGBREAK, signal.SIG_IGN)  # Only the runner handles the stop.
header = struct.Struct("<8s6I")
pixels = struct.pack("<H", pixel) * (width * height)
fd = os.open(frame, os.O_RDWR | os.O_CREAT | getattr(os, "O_BINARY", 0), 0o600)
os.write(fd, header.pack(b"Q66FRAME", 1, 0, width, height, width * 2, 1) + bytes(2 * 1024 * 1024))
with open(log, "a") as stream:
    sequence = 0
    while True:
        os.lseek(fd, 0, 0)
        os.write(fd, header.pack(b"Q66FRAME", 1, sequence + 1, width, height, width * 2, 1) + pixels)
        sequence += 2
        os.lseek(fd, 0, 0)
        os.write(fd, header.pack(b"Q66FRAME", 1, sequence, width, height, width * 2, 1))
        if sequence %% 60 == 0:
            stream.write("fake core: frame %%d\n" %% sequence)
            stream.flush()
        time.sleep(1 / 60)
'''


def prepare(runtime, work):
    """Create ``work/session`` (a minative session); return its directory and approval digest."""
    runtime, work = Path(os.path.realpath(runtime)), Path(os.path.realpath(work))
    if str(runtime) not in sys.path:
        sys.path.insert(0, str(runtime))
    if sys.platform == "win32":
        from azki_simulator import win32_compat
        win32_compat.install()
    from azki_simulator.minative import SCHEMA, _digest
    from azki_simulator.minative_bridge import create_minative_session
    project = work / "project"
    project.mkdir(parents=True, mode=0o700)
    bootstrap = project / "bootstrap.py"
    bootstrap.write_text(FAKE_RUNNER % {"runtime": str(runtime), "core": FAKE_CORE % (), "width": WIDTH,
                                        "height": HEIGHT, "pixel": PIXEL})
    identity = {"mode": "generated", "project": str(project), "prepared": {},
                "profile": {"id": PROFILE, "model": "fake.model"}}
    plan = {"schema": SCHEMA, "profileID": PROFILE, "width": WIDTH, "height": HEIGHT,
            "identity": identity, "approvalDigest": _digest(identity),
            "spec": {"python": sys.executable, "bootstrap": str(bootstrap), "project": str(project)}}
    descriptor = create_minative_session(work / "session", plan)
    return {"directory": descriptor["directory"], "identityDigest": plan["approvalDigest"],
            "width": WIDTH, "height": HEIGHT, "pixel": PIXEL}


if __name__ == "__main__":
    print(json.dumps(prepare(sys.argv[1], sys.argv[2])))
