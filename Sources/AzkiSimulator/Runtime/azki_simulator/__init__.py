"""Local-only Azki simulator bridge; no downloaded module imports."""
import sys

if sys.platform == "win32":
    # POSIX primitives (O_NOFOLLOW, dir_fd, flock, process groups) for Windows.
    from . import win32_compat as _win32_compat
    _win32_compat.install()

APPLICATION_VERSION = "0.1.0"
PROTOCOL_VERSION = 1
