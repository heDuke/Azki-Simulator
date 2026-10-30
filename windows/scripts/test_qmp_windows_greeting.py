# SPDX-License-Identifier: GPL-2.0-only
"""Regression: multi_device's runner.QMP wrapper must accept timeout=.

On Windows, runner.main waits for QEMU's QMP greeting through the whole
QMP_START_SECONDS window by calling ``QMP(path, timeout=remaining)``. The
wrapper multi_device installs used to be ``lambda path: QMP(path, timeout=2)``,
which raised ``TypeError: ... unexpected keyword argument 'timeout'``. The
runner then exited 1 with no result.json (missing_result); the App reported
that as a crash. QEMU itself was fine (qemu.log already had the factory
storage line).

The AST and path-only TypeError checks run everywhere. The delayed-greeting
checks exercise the real QMP client: AF_UNIX on POSIX, loopback TCP via
win32_compat on Windows (the production Windows path).
"""
from __future__ import annotations

import ast
import socket
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MULTI_DEVICE = ROOT / "engine" / "project" / "multi_device.py"
BACKEND = ROOT / "engine" / "project" / "backend"
RUNTIME = ROOT / "Sources" / "AzkiSimulator" / "Runtime"

GREETING = (
    b'{"QMP": {"version": {"qemu": {"micro": 0, "minor": 1, "major": 10},'
    b' "package": ""}, "capabilities": []}}\n'
)


def _runner_qmp_lambda(tree: ast.AST) -> ast.Lambda:
    found = None
    for node in ast.walk(tree):
        if not isinstance(node, ast.Assign):
            continue
        for target in node.targets:
            if (
                isinstance(target, ast.Attribute)
                and target.attr == "QMP"
                and isinstance(target.value, ast.Name)
                and target.value.id == "runner"
            ):
                found = node.value
    if found is None:
        raise AssertionError("multi_device.py must assign runner.QMP")
    if not isinstance(found, ast.Lambda):
        raise AssertionError(
            "runner.QMP assignment must be a lambda, got %s" % type(found).__name__
        )
    return found


def _install_production_wrapper():
    """Install the same runner.QMP lambda multi_device.main installs."""
    sys.path.insert(0, str(BACKEND))
    from src.qmp import QMP
    from src import runner

    runner.QMP = lambda path, timeout=2: QMP(path, timeout=timeout)
    return runner


def _serve_delayed_greeting(accept, delay, errors):
    def serve():
        try:
            conn = accept()
            try:
                time.sleep(delay)  # QEMU accepts before the greeting
                conn.sendall(GREETING)
                buf = b""
                while b"\n" not in buf:
                    chunk = conn.recv(4096)
                    if not chunk:
                        return
                    buf += chunk
                conn.sendall(b'{"return": {}}\n')
            finally:
                conn.close()
        except BaseException as error:  # noqa: BLE001 — surface on the test thread
            errors.append(error)

    return serve


class MultiDeviceQmpWrapperTests(unittest.TestCase):
    def test_wrapper_lambda_accepts_timeout(self):
        """The lambda multi_device installs must take timeout= (default 2)."""
        lambda_node = _runner_qmp_lambda(ast.parse(MULTI_DEVICE.read_text()))
        names = [arg.arg for arg in lambda_node.args.args]
        self.assertEqual(
            names,
            ["path", "timeout"],
            "runner.QMP lambda must accept (path, timeout=...); "
            "Windows greeting wait passes timeout=",
        )
        self.assertTrue(
            lambda_node.args.defaults
            and isinstance(lambda_node.args.defaults[-1], ast.Constant)
            and lambda_node.args.defaults[-1].value == 2,
            "timeout default must stay 2 so POSIX call sites are unchanged",
        )

    def test_path_only_wrapper_would_raise_typeerror(self):
        """Document the old bug: a path-only lambda rejects timeout=."""
        broken = lambda path: None  # noqa: E731 — intentional old shape
        with self.assertRaises(TypeError) as caught:
            broken(Path("unused"), timeout=5)
        self.assertIn("timeout", str(caught.exception))

    def test_delayed_greeting_honors_timeout_through_wrapper(self):
        """A greeting after 2 s still succeeds when timeout= is forwarded."""
        delay = 2.5
        runner = _install_production_wrapper()
        errors: list[BaseException] = []

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "qmp.sock"
            if sys.platform == "win32":
                sys.path.insert(0, str(RUNTIME))
                from azki_simulator import win32_compat

                win32_compat.install()
                server = win32_compat.listen(path, token=False)

                def accept():
                    conn, _ = server.accept()
                    return conn

            else:
                server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                server.bind(str(path))
                server.listen(1)

                def accept():
                    conn, _ = server.accept()
                    return conn

            thread = threading.Thread(
                target=_serve_delayed_greeting(accept, delay, errors), daemon=True
            )
            thread.start()
            try:
                # Call shape used by runner.main on Windows.
                connection = runner.QMP(path, timeout=max(2, 5))
                try:
                    self.assertIsNotNone(connection.socket)
                finally:
                    connection.close()
            finally:
                server.close()
                thread.join(timeout=5)
            self.assertEqual(errors, [])


if __name__ == "__main__":
    unittest.main()
