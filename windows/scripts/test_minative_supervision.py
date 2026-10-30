# SPDX-License-Identifier: GPL-2.0-only
"""The minative run supervision loop on real files, without firmware.

A fake runner stands in for multi_device.py + QEMU: it joins its own process
group (a Job Object on Windows), appends to qemu.log and publishes
lcd-frame.raw at 60 Hz with a temporary file + os.replace, like the real
core. The adapter's own start/_watch/_check_outputs/stop path supervises it.

On Windows, os.DirEntry.stat() reports st_nlink == 0 (and st_ino/st_dev 0);
_check_outputs needs st_nlink == 1, so before win32_compat asked the file
itself every real run failed at once with "output_limit".

BridgeSupervisionTests goes further down the real path with fake_minative's
runner: the MinativeSessionBridge start (on Windows through the real
minative_launch.py supervisor and its Job Object), control.json/identity
checks with the runner/core process identity, the input endpoint with its
token and peer check, the frame mirror into frame.raw, status latency while
frames are mirrored, and the stop.

Run by run-windows-tests.py (Windows CI); it also passes on Linux.
"""
import contextlib
import json
import os
from pathlib import Path
import shutil
import signal
import stat
import struct
import sys
import tempfile
import time
import threading
import unittest
from unittest import mock

RUNTIME = Path(__file__).resolve().parents[2] / "Sources/AzkiSimulator/Runtime"
sys.path.insert(0, str(RUNTIME))
if sys.platform == "win32":
    from azki_simulator import win32_compat
    win32_compat.install()
from azki_simulator import minative  # noqa: E402
sys.path.insert(0, str(Path(__file__).resolve().parent))
import fake_minative  # noqa: E402

WIDTH, HEIGHT = 192, 490

FAKE_RUNNER = r"""
import os, signal, struct, sys, time
from pathlib import Path
sys.path.insert(0, sys.argv[1])
if sys.platform == "win32":
    from azki_simulator import win32_compat
    win32_compat.install()
    os.setsid()  # The real runner's process group: a Job Object here.
run, width, height = Path(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
stopping = []
signal.signal(signal.SIGBREAK if sys.platform == "win32" else signal.SIGINT, lambda *_: stopping.append(1))

def write(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)

(run / "tmp/q66-qmp-fake").mkdir(mode=0o700)
log = os.fdopen(os.open(run / "qemu.log", os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600), "w")
pixels = bytes(width * height * 2)
sequence, end = 0, time.monotonic() + 60
while not stopping and time.monotonic() < end:
    sequence += 2
    temporary = run / ".lcd-frame.tmp"
    write(temporary, struct.pack("<8s6I", b"Q66FRAME", 1, sequence, width, height, width * 2, 1) + pixels)
    os.replace(temporary, run / "lcd-frame.raw")
    log.write("frame %d\n" % sequence)
    log.flush()
    print("published", sequence, flush=True)
    time.sleep(1 / 60)
log.close()
write(run / "result.json", b'{"outcome": "interrupted"}')
"""


class FakeRunnerAdapter(minative.MinativeAdapter):
    def launch_spec(self):
        env = {"PYTHONDONTWRITEBYTECODE": "1"}
        if sys.platform == "win32":
            system_root = os.environ.get("SYSTEMROOT", r"C:\Windows")
            env.update(SYSTEMROOT=system_root, PATH=system_root + r"\System32;" + system_root)
        return {"command": [sys.executable, "-I", "-S", "-B", "-c", FAKE_RUNNER, str(RUNTIME), str(self.run),
                            str(WIDTH), str(HEIGHT)],
                "env": env, "cwd": str(self.run)}


class SupervisionTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(os.path.realpath(tempfile.mkdtemp(prefix="azki-supervision-")))
        os.chmod(self.root, 0o700)
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def test_scandir_stat_reports_links_and_identity(self):
        path = self.root / "file.bin"
        path.write_bytes(b"x")
        with os.scandir(self.root) as entries:
            entry = next(entries)
            listed = entry.stat(follow_symlinks=False)
        actual = os.lstat(path)
        self.assertEqual(listed.st_nlink, 1)
        self.assertEqual((listed.st_dev, listed.st_ino), (actual.st_dev, actual.st_ino))
        self.assertNotEqual(listed.st_ino, 0)
        self.assertEqual(stat.S_IMODE(listed.st_mode), stat.S_IMODE(actual.st_mode))

    def test_watch_loop_supervises_a_publishing_runner(self):
        digest = "0" * 64
        plan = {"profileID": "fake", "width": WIDTH, "height": HEIGHT, "approvalDigest": digest, "identity": {}}
        with mock.patch.object(minative, "revalidate_plan", lambda plan: None):
            adapter = FakeRunnerAdapter(plan, self.root, digest, isolation_verified=True, fixture_only=True)
            passes = []
            check = adapter._check_outputs

            def counted():
                check()
                passes.append(time.monotonic())
            adapter._check_outputs = counted
            adapter.start()
        try:
            deadline = time.monotonic() + 30
            while len(passes) < 4 and adapter.process.poll() is None and time.monotonic() < deadline:
                time.sleep(.1)
            status = adapter.status()
            self.assertIsNone(status["reason"], status)
            self.assertIsNone(adapter.process.poll(), "the adapter stopped its runner")
            self.assertGreaterEqual(len(passes), 4, "output checks did not run once a second")
            with open(adapter.run / "lcd-frame.raw", "rb") as stream:
                header = struct.unpack("<8s6I", stream.read(32))
            self.assertEqual(header[0], b"Q66FRAME")
            self.assertGreater(header[2], 0)
            self.assertGreater((adapter.run / "qemu.log").stat().st_size, 0)
        finally:
            status = adapter.stop()
        self.assertEqual((status["state"], status["reason"]), ("stopped", "stopped"), status)
        self.assertIsInstance(status["lastExit"], int)
        self.assertEqual(status["cleanup"], "reaped", status)
        if sys.platform == "win32":
            self.assertEqual(win32_compat.group_members(adapter.process.pid), [])


class BridgeSupervisionTests(unittest.TestCase):
    def setUp(self):
        self.work = Path(os.path.realpath(tempfile.mkdtemp(prefix="azki-bridge-")))
        os.chmod(self.work, 0o700)
        self.addCleanup(shutil.rmtree, self.work, ignore_errors=True)

    def launch_policy(self):
        """Windows: the production launch (minative_launch.py in a Job Object).
        Elsewhere the developer sandbox is macOS-only, so the runner starts directly."""
        if sys.platform == "win32":
            return contextlib.nullcontext()
        start = minative.MinativeAdapter.start

        def direct(adapter):
            adapter.execution_prefix, adapter.fixture_only = (), True
            return start(adapter)
        from azki_simulator import minative_isolation
        patches = [mock.patch.object(minative_isolation, "execution_prefix", lambda adapter: ()),
                   mock.patch.object(minative.MinativeAdapter, "start", direct)]

        class Both:
            def __enter__(self):
                for item in patches:
                    item.start()

            def __exit__(self, *_):
                for item in patches:
                    item.stop()
        return Both()

    def test_bridge_runs_the_fake_runner_end_to_end(self):
        from azki_simulator.minative_bridge import MinativeSessionBridge
        prepared = fake_minative.prepare(RUNTIME, self.work)
        directory = Path(prepared["directory"])
        bridge = MinativeSessionBridge(directory, prepared["identityDigest"])
        with self.launch_policy():
            started = time.monotonic()
            status = bridge.start()
        try:
            self.assertEqual(status["state"], "running", status)
            deadline = time.monotonic() + 60
            while bridge.adapter.status()["state"] != "running" and time.monotonic() < deadline:
                self.assertIsNone(bridge.adapter.status()["reason"], bridge.adapter.status())
                time.sleep(.05)
            adapter = bridge.adapter
            self.assertEqual(adapter.status()["state"], "running", adapter.status())
            print("\ncontrol.json accepted after %.2f s" % (time.monotonic() - started))
            if sys.platform == "win32":
                identity = adapter._developer_process_identity
                self.assertEqual(identity["runnerPID"], adapter._runner_pid())
                self.assertIn(identity["corePID"], win32_compat.group_members(adapter.process.pid))

            frame = directory / "frame.raw"
            deadline = time.monotonic() + 30
            while not frame.exists() and time.monotonic() < deadline:
                time.sleep(.02)
            with open(frame, "rb") as stream:
                data = stream.read()
            magic, version, sequence, width, height, stride, fmt = struct.unpack("<8s6I", data[:32])
            self.assertEqual((magic, width, height, stride, fmt), (b"Q66FRAME", WIDTH_P67, HEIGHT_P67, WIDTH_P67 * 2, 1))
            self.assertEqual(sequence % 2, 0)
            self.assertEqual(data[32:34], struct.pack("<H", fake_minative.PIXEL))

            # The bridge answers each request on its main thread while the frame
            # worker mirrors at 60 Hz; both share the adapter lock.
            costs = []
            import cProfile
            import io
            import pstats
            profile = cProfile.Profile()
            for _ in range(20):
                begin = time.perf_counter()
                profile.enable()
                adapter.read_frame()
                profile.disable()
                costs.append(time.perf_counter() - begin)
            listing = io.StringIO()
            pstats.Stats(profile, stream=listing).sort_stats("cumulative").print_stats(18)
            print("read_frame profile (20 calls):\n" + listing.getvalue()[-4000:])
            latencies = []
            end = time.monotonic() + 3
            while time.monotonic() < end:
                begin = time.perf_counter()
                bridge.status()
                latencies.append(time.perf_counter() - begin)
                time.sleep(.1)
            metrics = dict(bridge._frame_metrics)
            print("read_frame median %.1f ms, max %.1f ms; status latency max %.1f ms; frame metrics %s" % (
                sorted(costs)[len(costs) // 2] * 1e3, max(costs) * 1e3, max(latencies) * 1e3, metrics))
            self.assertLess(max(latencies), 1.0, "status starved while frames were mirrored")
            self.assertGreater(metrics["published"], 10)

            self.assertEqual(bridge.input({"x": 10, "y": 20, "down": True})["accepted"], True)
            self.assertEqual(bridge.input({"x": 10, "y": 20, "down": False})["accepted"], True)
            events = (adapter.run / "fake-input.log").read_text().splitlines()
            self.assertEqual([json.loads(line)["down"] for line in events], [True, False])
        finally:
            stopped = bridge.stop()
        self.assertEqual(stopped["state"], "stopped", stopped)
        self.assertEqual(stopped["stopReason"], "stopped", stopped)
        self.assertEqual(stopped["cleanup"], "reaped", stopped)
        if sys.platform == "win32":
            self.assertEqual(win32_compat.group_members(bridge.adapter.process.pid), [])


WIDTH_P67, HEIGHT_P67 = fake_minative.WIDTH, fake_minative.HEIGHT


if __name__ == "__main__":
    unittest.main()
