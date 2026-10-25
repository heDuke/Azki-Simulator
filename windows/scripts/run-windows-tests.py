# SPDX-License-Identifier: GPL-2.0-only
"""Run the upstream Python unit tests that apply to Windows.

Modules here are pure Python (their C parts skip without a GLib compiler).
Excluded, because they exercise POSIX-only primitives: test_developer_storage
(Darwin renameatx_np), test_gpu_*/test_touch_sleep and
test_production_imu_sample_registers (they compile C against host GLib; a
runner's unrelated pkg-config would make them error rather than skip), and
test_rejects_symlink_hardlink_and_nonregular_file (os.mkfifo; symlinks need
Developer Mode on Windows).

test_win32_replace (beside this script) checks the Windows-only os.replace of
win32_compat while frame.raw is being read; test_minative_supervision runs the
minative start/watch/output-check/stop loop over a fake runner on NTFS.
"""
from pathlib import Path
import sys
import unittest

TESTS = Path(__file__).resolve().parents[2] / "tests"
MODULES = ("test_frame_raw", "test_device_parameters", "test_orientation")
SKIPPED = {"test_rejects_symlink_hardlink_and_nonregular_file", "test_production_imu_sample_registers"}


def flatten(suite):
    for item in suite:
        if isinstance(item, unittest.TestSuite):
            yield from flatten(item)
        else:
            yield item


sys.path.insert(0, str(TESTS))
sys.path.insert(0, str(Path(__file__).resolve().parent))
loaded = unittest.defaultTestLoader.loadTestsFromNames(MODULES + ("test_win32_replace", "test_minative_supervision"))
suite = unittest.TestSuite(test for test in flatten(loaded) if test._testMethodName not in SKIPPED)
result = unittest.TextTestRunner(verbosity=2).run(suite)
sys.exit(0 if result.wasSuccessful() else 1)
