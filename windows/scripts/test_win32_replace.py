# SPDX-License-Identifier: GPL-2.0-only
"""Windows only: os.replace under win32_compat while frame.raw is being read.

minative_bridge.py publishes frame.raw with os.replace up to 60 times a second
and the app reads it concurrently. Run by run-windows-tests.py.
"""
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "Sources/AzkiSimulator/Runtime"))
if sys.platform == "win32":
    from azki_simulator import win32_compat
    win32_compat.install()


@unittest.skipUnless(sys.platform == "win32", "Windows only")
class ReplaceWhileReadTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="azki-replace-"))
        self.target = self.root / "frame.raw"
        self.target.write_bytes(b"old")

    def publish(self, data):
        temporary = self.root / (".frame-" + os.urandom(6).hex())
        temporary.write_bytes(data)
        os.replace(temporary, self.target)

    def test_replace_succeeds_while_a_delete_sharing_reader_holds_the_file(self):
        # win32_compat's os.open (like the app's FileShare.ReadWrite | FileShare.Delete) shares delete.
        fd = os.open(self.target, os.O_RDONLY)
        try:
            started = time.monotonic()
            self.publish(b"new")
            self.assertLess(time.monotonic() - started, 0.2, "replace waited for the reader")
            self.assertEqual(os.pread(fd, 3, 0), b"old")  # the reader keeps the frame it opened
        finally:
            os.close(fd)
        self.assertEqual(self.target.read_bytes(), b"new")

    def test_many_replaces_with_a_concurrent_reader(self):
        import threading
        stop = threading.Event()

        def read():
            while not stop.is_set():
                try:
                    fd = os.open(self.target, os.O_RDONLY)
                except (FileNotFoundError, PermissionError):
                    continue  # the app's FrameReader treats these as "no frame yet"
                try:
                    os.pread(fd, 16, 0)
                finally:
                    os.close(fd)

        reader = threading.Thread(target=read)
        reader.start()
        try:
            for index in range(300):
                self.publish(b"frame %d" % index)
        finally:
            stop.set()
            reader.join()
        self.assertEqual(self.target.read_bytes(), b"frame 299")


if __name__ == "__main__":
    unittest.main()
