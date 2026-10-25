"""Windows equivalents of the POSIX primitives this runtime depends on.

Imported and installed only when ``sys.platform == "win32"`` (see
``__init__.py`` and ``engine/bootstrap.py``); macOS and Linux never load this
module, so their behavior is unchanged.

The runtime opens private files and directories with ``O_NOFOLLOW`` /
``O_DIRECTORY`` and ``dir_fd``, compares owners and POSIX modes, locks files
with ``fcntl.flock``, limits resources with ``resource`` and manages process
groups. Windows Python has none of these. Instead of rewriting every call
site, ``install()`` provides them with Windows semantics:

* ``os.open`` uses ``CreateFileW`` with full sharing (files can be renamed or
  deleted while open, as on POSIX) and is always binary. ``O_NOFOLLOW``
  rejects a final reparse point (symbolic link or junction); the Darwin
  ``O_NOFOLLOW_ANY`` bit rejects one anywhere in the path. ``O_DIRECTORY``
  returns a descriptor for a directory handle usable as ``dir_fd``.
* ``dir_fd`` is accepted by the ``os`` functions the runtime uses.
* Ownership: the per-user data directories are protected by their ACLs, so
  ``os.getuid()`` is 0 like Windows ``st_uid`` and POSIX permission bits are
  reported as private (0700 directories and executables, 0600 files).
* ``fcntl.flock`` uses ``LockFileEx`` on a byte far beyond the end of the file
  (an advisory lock that never blocks ordinary reads or writes);
  ``resource.setrlimit`` is accepted (limits come from the Job Object).
* Process groups are named Job Objects; ``os.kill(pid, 0)`` only probes.
* Unix-socket endpoints become loopback TCP: an endpoint file at the socket
  path holds the port and a per-endpoint random token (see ``listen``,
  ``accept`` and ``connect``). The peer PID of a loopback connection comes
  from the kernel TCP table (psutil).
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
import errno
import json
import os
import secrets
import socket
import stat
import sys
import time
import types

if sys.platform != "win32":  # pragma: no cover - imported only on Windows
    raise ImportError("win32_compat is only for Windows")

import msvcrt

O_NOFOLLOW = 0x00100000
O_DIRECTORY = 0x00200000
O_NONBLOCK = 0x00400000
O_CLOEXEC = 0x00800000
O_NOFOLLOW_ANY = 0x20000000  # the literal Darwin bit used by the runtime
_PRIVATE_FLAGS = O_NOFOLLOW | O_DIRECTORY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW_ANY

_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GENERIC_READ, GENERIC_WRITE = 0x80000000, 0x40000000
FILE_READ_ATTRIBUTES, FILE_LIST_DIRECTORY, SYNCHRONIZE = 0x80, 0x1, 0x100000
FILE_SHARE_ALL = 0x1 | 0x2 | 0x4
CREATE_NEW, CREATE_ALWAYS, OPEN_EXISTING, OPEN_ALWAYS, TRUNCATE_EXISTING = 1, 2, 3, 4, 5
FILE_ATTRIBUTE_READONLY, FILE_ATTRIBUTE_DIRECTORY = 0x1, 0x10
FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_REPARSE_POINT = 0x80, 0x400
FILE_ATTRIBUTE_TEMPORARY, FILE_FLAG_DELETE_ON_CLOSE, DELETE = 0x100, 0x04000000, 0x00010000
FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT = 0x02000000, 0x00200000
INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value
ERROR_HANDLE_EOF = 38


class _ByHandleInfo(ctypes.Structure):
    _fields_ = [("dwFileAttributes", wintypes.DWORD), ("ftCreationTime", wintypes.FILETIME),
                ("ftLastAccessTime", wintypes.FILETIME), ("ftLastWriteTime", wintypes.FILETIME),
                ("dwVolumeSerialNumber", wintypes.DWORD), ("nFileSizeHigh", wintypes.DWORD),
                ("nFileSizeLow", wintypes.DWORD), ("nNumberOfLinks", wintypes.DWORD),
                ("nFileIndexHigh", wintypes.DWORD), ("nFileIndexLow", wintypes.DWORD)]


class _Overlapped(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_void_p), ("InternalHigh", ctypes.c_void_p),
                ("Offset", wintypes.DWORD), ("OffsetHigh", wintypes.DWORD), ("hEvent", wintypes.HANDLE)]


_kernel32.CreateFileW.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE)
_kernel32.CreateFileW.restype = wintypes.HANDLE
_kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
_kernel32.GetFileInformationByHandle.argtypes = (wintypes.HANDLE, ctypes.POINTER(_ByHandleInfo))
_kernel32.ReadFile.argtypes = (wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
                               ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(_Overlapped))
_kernel32.WriteFile.argtypes = _kernel32.ReadFile.argtypes
_kernel32.LockFileEx.argtypes = (wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                 wintypes.DWORD, ctypes.POINTER(_Overlapped))
_kernel32.UnlockFileEx.argtypes = (wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                   ctypes.POINTER(_Overlapped))
_kernel32.CreateJobObjectW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR)
_kernel32.CreateJobObjectW.restype = wintypes.HANDLE
_kernel32.OpenJobObjectW.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR)
_kernel32.OpenJobObjectW.restype = wintypes.HANDLE
_kernel32.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
_kernel32.TerminateJobObject.argtypes = (wintypes.HANDLE, wintypes.UINT)
_kernel32.QueryInformationJobObject.argtypes = (wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                                wintypes.DWORD, ctypes.POINTER(wintypes.DWORD))
_kernel32.SetInformationJobObject.argtypes = (wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD)
_kernel32.GetCurrentProcess.restype = wintypes.HANDLE
_kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
_kernel32.OpenProcess.restype = wintypes.HANDLE
_kernel32.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))

_native = {}
_directories = {}  # descriptor -> absolute directory path
_installed = False


def _error(path=None, code=None):
    code = ctypes.get_last_error() if code is None else code
    return OSError(None, ctypes.FormatError(code).strip(), path, code)


def _absolute(path):
    path = os.fspath(path)
    if isinstance(path, bytes):
        path = os.fsdecode(path)
    return _native["abspath"](path)


def _join(path, dir_fd):
    """Resolve a POSIX ``*at`` (path, dir_fd) pair to an absolute path."""
    path = os.fspath(path)
    if isinstance(path, bytes):
        path = os.fsdecode(path)
    if dir_fd is None or os.path.isabs(path):
        return path
    try:
        base = _directories[dir_fd]
    except KeyError:
        raise OSError(errno.EBADF, "Not a directory descriptor", str(dir_fd)) from None
    return os.path.join(base, path) if path not in ("", ".") else base


def _attributes(path):
    """Attributes of ``path`` itself (a reparse point is not followed)."""
    try:
        return _native["lstat"](path).st_file_attributes
    except FileNotFoundError:
        return None


def _reject_reparse_components(path):
    """O_NOFOLLOW_ANY: no symbolic link or junction anywhere in the path."""
    drive, rest = os.path.splitdrive(path)
    current = drive + os.sep
    for part in [p for p in rest.replace("/", "\\").split("\\") if p]:
        current = os.path.join(current, part)
        attributes = _attributes(current)
        if attributes is None:
            return
        if attributes & FILE_ATTRIBUTE_REPARSE_POINT:
            raise OSError(errno.ELOOP, "Path contains a reparse point", current)


def _handle_info(handle):
    info = _ByHandleInfo()
    if not _kernel32.GetFileInformationByHandle(handle, ctypes.byref(info)):
        raise _error()
    return info


def _open(path, flags, mode=0o777, *, dir_fd=None):
    path = _absolute(_join(path, dir_fd))
    nofollow = bool(flags & (O_NOFOLLOW | O_NOFOLLOW_ANY))
    if flags & O_NOFOLLOW_ANY:
        _reject_reparse_components(path)
    directory = bool(flags & O_DIRECTORY)
    flags &= ~_PRIVATE_FLAGS
    access_mode = flags & (os.O_RDONLY | os.O_WRONLY | os.O_RDWR)
    if directory:
        access, disposition = FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, OPEN_EXISTING
    else:
        access = {os.O_RDONLY: GENERIC_READ, os.O_WRONLY: GENERIC_WRITE,
                  os.O_RDWR: GENERIC_READ | GENERIC_WRITE}[access_mode]
        creating = flags & os.O_CREAT
        if creating and flags & os.O_EXCL:
            disposition = CREATE_NEW
        elif creating and flags & os.O_TRUNC:
            disposition = CREATE_ALWAYS
        elif creating:
            disposition = OPEN_ALWAYS
        elif flags & os.O_TRUNC:
            disposition = TRUNCATE_EXISTING
        else:
            disposition = OPEN_EXISTING
    attributes = FILE_ATTRIBUTE_NORMAL if mode & 0o200 else FILE_ATTRIBUTE_READONLY
    attributes |= FILE_FLAG_BACKUP_SEMANTICS
    if nofollow:
        attributes |= FILE_FLAG_OPEN_REPARSE_POINT
    if flags & getattr(os, "O_TEMPORARY", 0):
        attributes |= FILE_FLAG_DELETE_ON_CLOSE
        access |= DELETE
    if flags & getattr(os, "O_SHORT_LIVED", 0):
        attributes |= FILE_ATTRIBUTE_TEMPORARY
    handle = _kernel32.CreateFileW(path, access, FILE_SHARE_ALL, None, disposition, attributes, None)
    if handle == INVALID_HANDLE_VALUE or handle is None:
        raise _error(path)
    try:
        info = _handle_info(handle)
        if nofollow and info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT:
            raise OSError(errno.ELOOP, "Too many levels of symbolic links", path)
        is_directory = bool(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
        if directory and not is_directory:
            raise NotADirectoryError(errno.ENOTDIR, "Not a directory", path)
        if is_directory and access_mode != os.O_RDONLY:
            raise IsADirectoryError(errno.EISDIR, "Is a directory", path)
        crt_flags = (os.O_RDONLY if access_mode == os.O_RDONLY else 0) | (flags & (os.O_APPEND | os.O_TEXT))
        descriptor = msvcrt.open_osfhandle(handle, crt_flags)
    except BaseException:
        _kernel32.CloseHandle(handle)
        raise
    if is_directory:
        _directories[descriptor] = path
    else:
        _directories.pop(descriptor, None)
    return descriptor


def _close(fd):
    _directories.pop(fd, None)
    return _native["close"](fd)


def _private_mode(mode):
    kind = stat.S_IFMT(mode)
    if kind == stat.S_IFDIR:
        permissions = 0o700
    elif kind == stat.S_IFREG:
        permissions = 0o700 if mode & 0o111 else 0o600
    else:
        permissions = stat.S_IMODE(mode) & 0o700
    return kind | permissions


_EXTRA_STAT_FIELDS = ("st_atime", "st_mtime", "st_ctime", "st_atime_ns", "st_mtime_ns", "st_ctime_ns",
                      "st_birthtime", "st_birthtime_ns", "st_file_attributes", "st_reparse_tag")


def _normalized(result):
    values = list(result[:10])
    values[0] = _private_mode(result.st_mode)
    extra = {name: getattr(result, name) for name in _EXTRA_STAT_FIELDS if hasattr(result, name)}
    return os.stat_result(values, extra)


def _stat(path, *, dir_fd=None, follow_symlinks=True):
    if isinstance(path, int):
        return _fstat(path)
    return _normalized(_native["stat"](_join(path, dir_fd), follow_symlinks=follow_symlinks))


def _lstat(path, *, dir_fd=None):
    return _normalized(_native["lstat"](_join(path, dir_fd)))


def _fstat(fd):
    return _normalized(_native["fstat"](fd))


def _mkdir(path, mode=0o777, *, dir_fd=None):
    return _native["mkdir"](_join(path, dir_fd), mode)


def _rmdir(path, *, dir_fd=None):
    return _native["rmdir"](_join(path, dir_fd))


def _unlink(path, *, dir_fd=None):
    return _native["unlink"](_join(path, dir_fd))


def _retry_sharing(function, *args):
    # A concurrent reader without FILE_SHARE_DELETE briefly blocks a rename.
    for attempt in range(50):
        try:
            return function(*args)
        except PermissionError:
            if attempt == 49:
                raise
            time.sleep(.01)


def _rename(src, dst, *, src_dir_fd=None, dst_dir_fd=None):
    return _retry_sharing(_native["rename"], _join(src, src_dir_fd), _join(dst, dst_dir_fd))


def _replace(src, dst, *, src_dir_fd=None, dst_dir_fd=None):
    return _retry_sharing(_native["replace"], _join(src, src_dir_fd), _join(dst, dst_dir_fd))


def _link(src, dst, *, src_dir_fd=None, dst_dir_fd=None, follow_symlinks=True):
    # CreateHardLinkW never follows a final reparse point.
    return _native["link"](_join(src, src_dir_fd), _join(dst, dst_dir_fd))


def _readlink(path, *, dir_fd=None):
    return _native["readlink"](_join(path, dir_fd))


def _chmod(path, mode, *, dir_fd=None, follow_symlinks=True):
    if isinstance(path, int):
        return None
    return _native["chmod"](_join(path, dir_fd), mode)


def _fchmod(fd, mode):
    """POSIX modes have no Windows equivalent; the directory ACL applies."""
    _native["fstat"](fd)  # still fails for an invalid descriptor


def _utime(path, times=None, *, ns=None, dir_fd=None, follow_symlinks=True):
    target = path if isinstance(path, int) else _join(path, dir_fd)
    if ns is not None:
        return _native["utime"](target, ns=ns)
    return _native["utime"](target, times)


def _fsync(fd):
    if fd in _directories:
        return None  # NTFS directory metadata is journaled; no handle flush exists
    try:
        return _native["fsync"](fd)
    except OSError as error:
        if error.errno == errno.EBADF:
            _native["fstat"](fd)  # a read-only descriptor has nothing to flush
            return None
        raise


def _listdir(path="."):
    if isinstance(path, int):
        path = _directories.get(path)
        if path is None:
            raise OSError(errno.EBADF, "Not a directory descriptor")
    return _native["listdir"](path)


class _DirEntry:
    """``os.DirEntry`` with private-mode ``stat()``; ``path`` is the name for a dir_fd."""

    __slots__ = ("_entry", "name", "path")

    def __init__(self, entry, by_descriptor):
        self._entry = entry
        self.name = entry.name
        self.path = entry.name if by_descriptor else entry.path

    def stat(self, *, follow_symlinks=True):
        return _normalized(self._entry.stat(follow_symlinks=follow_symlinks))

    def is_dir(self, *, follow_symlinks=True):
        return self._entry.is_dir(follow_symlinks=follow_symlinks)

    def is_file(self, *, follow_symlinks=True):
        return self._entry.is_file(follow_symlinks=follow_symlinks)

    def is_symlink(self):
        return self._entry.is_symlink()

    def is_junction(self):
        return self._entry.is_junction()

    def inode(self):
        return self._entry.inode()

    def __fspath__(self):
        return self.path

    def __repr__(self):
        return f"<DirEntry {self.name!r}>"


class _ScandirIterator:
    def __init__(self, iterator, by_descriptor):
        self._iterator, self._by_descriptor = iterator, by_descriptor

    def __iter__(self):
        return self

    def __next__(self):
        return _DirEntry(next(self._iterator), self._by_descriptor)

    def close(self):
        self._iterator.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def _scandir(path="."):
    by_descriptor = isinstance(path, int)
    if by_descriptor:
        directory = _directories.get(path)
        if directory is None:
            raise OSError(errno.EBADF, "Not a directory descriptor")
        path = directory
    return _ScandirIterator(_native["scandir"](path), by_descriptor)


def _transfer(fd, buffer, count, offset, write):
    handle = msvcrt.get_osfhandle(fd)
    position = _Overlapped(Offset=offset & 0xFFFFFFFF, OffsetHigh=offset >> 32)
    done = wintypes.DWORD()
    function = _kernel32.WriteFile if write else _kernel32.ReadFile
    if not function(handle, buffer, count, ctypes.byref(done), ctypes.byref(position)):
        code = ctypes.get_last_error()
        if not write and code == ERROR_HANDLE_EOF:
            return 0
        raise _error(code=code)
    return done.value


def _pread(fd, length, offset):
    buffer = ctypes.create_string_buffer(length)
    count = _transfer(fd, buffer, length, offset, False)
    return buffer.raw[:count]


def _pwrite(fd, data, offset):
    data = bytes(data)
    return _transfer(fd, data, len(data), offset, True)


def _getuid():
    return 0


# Process groups as named Job Objects. A group leader calls setsid(); every
# process it starts afterwards belongs to the job "Local\\AzkiSimulator-group-<pid>".
JOB_QUERY, JOB_TERMINATE, JOB_ALL = 0x0004, 0x0008, 0x1F001F
_group_job = None


def _job_name(pid):
    return f"Local\\AzkiSimulator-group-{int(pid)}"


def _open_job(pid, access=JOB_QUERY | JOB_TERMINATE):
    handle = _kernel32.OpenJobObjectW(access, False, _job_name(pid))
    return handle or None


class _JobProcessList(ctypes.Structure):
    _fields_ = [("NumberOfAssignedProcesses", wintypes.DWORD), ("NumberOfProcessIdsInList", wintypes.DWORD),
                ("ProcessIdList", ctypes.c_size_t * 4096)]


def group_members(pid):
    """Live process IDs in the group led by ``pid``; [] if there is none."""
    handle = _open_job(pid, JOB_QUERY)
    if handle is None:
        return []
    try:
        listing = _JobProcessList()
        if not _kernel32.QueryInformationJobObject(handle, 3, ctypes.byref(listing), ctypes.sizeof(listing), None):
            raise _error()
        return [int(listing.ProcessIdList[i]) for i in range(listing.NumberOfProcessIdsInList)]
    finally:
        _kernel32.CloseHandle(handle)


def setsid():
    """Become the leader of a new process group (a named Job Object)."""
    global _group_job
    if _group_job is not None:
        return os.getpid()
    handle = _kernel32.CreateJobObjectW(None, _job_name(os.getpid()))
    if not handle:
        raise _error()
    if not _kernel32.AssignProcessToJobObject(handle, _kernel32.GetCurrentProcess()):
        error = _error()
        _kernel32.CloseHandle(handle)
        raise error
    _group_job = handle  # kept open for the lifetime of the leader
    return os.getpid()


def _getpgrp():
    return os.getpid() if _group_job is not None else 0


def _getpgid(pid):
    if pid == 0 or pid == os.getpid():
        return _getpgrp() or os.getpid()
    import psutil
    try:
        candidates = [pid] + [parent.pid for parent in psutil.Process(pid).parents()[:8]]
    except psutil.NoSuchProcess:
        raise ProcessLookupError(errno.ESRCH, "No such process") from None
    for candidate in candidates:
        if pid in group_members(candidate):
            return candidate
    return pid


def _process_alive(pid):
    handle = _kernel32.OpenProcess(0x1000, False, int(pid))  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ctypes.get_last_error() == 5  # access denied: it exists
    try:
        code = wintypes.DWORD()
        return bool(_kernel32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259
    finally:
        _kernel32.CloseHandle(handle)


def _kill(pid, sig):
    if sig == 0:
        # Windows os.kill(pid, 0) would call TerminateProcess(pid, 0).
        if not _process_alive(pid):
            raise ProcessLookupError(errno.ESRCH, "No such process")
        return None
    return _native["kill"](pid, sig)


def _killpg(pgid, sig):
    handle = _open_job(pgid)
    if handle is None:
        raise ProcessLookupError(errno.ESRCH, "No such process group")
    try:
        if sig == 0:
            if not group_members(pgid):
                raise ProcessLookupError(errno.ESRCH, "No such process group")
            return None
        if not _kernel32.TerminateJobObject(handle, 128 + int(sig)):
            raise _error()
    finally:
        _kernel32.CloseHandle(handle)


# fcntl.flock as an advisory LockFileEx lock on one byte far past any data.
_LOCK_OFFSET = 0x7FFFFFFF_00000000


def _flock(fd, operation):
    fd = fd if isinstance(fd, int) else fd.fileno()
    handle = msvcrt.get_osfhandle(fd)
    region = _Overlapped(Offset=_LOCK_OFFSET & 0xFFFFFFFF, OffsetHigh=_LOCK_OFFSET >> 32)
    if operation & 8:  # LOCK_UN
        if not _kernel32.UnlockFileEx(handle, 0, 1, 0, ctypes.byref(region)):
            raise _error()
        return None
    flags = (0x2 if operation & 2 else 0) | (0x1 if operation & 4 else 0)
    if not _kernel32.LockFileEx(handle, flags, 0, 1, 0, ctypes.byref(region)):
        code = ctypes.get_last_error()
        if code in (33, 158):  # ERROR_LOCK_VIOLATION, ERROR_NOT_LOCKED
            raise BlockingIOError(errno.EWOULDBLOCK, "Resource temporarily unavailable")
        raise _error(code=code)
    return None


def _fcntl_call(fd, command, argument=0):
    if command == 51:  # F_FULLFSYNC
        _fsync(fd if isinstance(fd, int) else fd.fileno())
        return 0
    raise OSError(errno.EINVAL, "Unsupported fcntl command on Windows")


def _fcntl_module():
    module = types.ModuleType("fcntl", "Windows emulation of the fcntl calls used by Azki Simulator.")
    module.LOCK_SH, module.LOCK_EX, module.LOCK_NB, module.LOCK_UN = 1, 2, 4, 8
    module.F_FULLFSYNC = 51
    module.flock = _flock
    module.fcntl = _fcntl_call
    return module


def _resource_module():
    module = types.ModuleType("resource", "Windows: limits are enforced by the launcher's Job Object.")
    module.RLIM_INFINITY = -1
    for index, name in enumerate(("RLIMIT_CORE", "RLIMIT_CPU", "RLIMIT_FSIZE", "RLIMIT_NOFILE",
                                  "RLIMIT_AS", "RLIMIT_DATA", "RLIMIT_NPROC", "RLIMIT_STACK", "RLIMIT_MEMLOCK")):
        setattr(module, name, index)
    module.error = OSError
    module.getrlimit = lambda resource: (-1, -1)
    module.setrlimit = lambda resource, limits: None
    return module


# Unix-socket endpoints as loopback TCP plus an endpoint file and token.
ENDPOINT_SCHEMA = "azki.endpoint.v1"
TOKEN_BYTES = 32


def write_endpoint(path, port, token=None):
    """Publish an endpoint file exclusively at ``path`` (the old socket path)."""
    record = {"schema": ENDPOINT_SCHEMA, "transport": "tcp", "host": "127.0.0.1", "port": int(port),
              "token": token, "pid": os.getpid()}
    data = (json.dumps(record) + "\n").encode()
    temporary = str(path) + ".tmp-" + secrets.token_hex(6)
    fd = _open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW, 0o600)
    try:
        os.write(fd, data)
    finally:
        _close(fd)
    try:
        _native["rename"](temporary, os.fspath(path))  # fails if the endpoint exists
    except BaseException:
        _native["unlink"](temporary)
        raise


def read_endpoint(path):
    with open(path, "rb") as stream:
        data = stream.read(4097)
    record = json.loads(data)
    if (not isinstance(record, dict) or record.get("schema") != ENDPOINT_SCHEMA or record.get("transport") != "tcp"
            or record.get("host") != "127.0.0.1" or type(record.get("port")) is not int
            or not 0 < record["port"] < 65536
            or not (record.get("token") is None or (isinstance(record["token"], str)
                                                    and len(record["token"]) == 2 * TOKEN_BYTES))):
        raise OSError(errno.EINVAL, "Invalid loopback endpoint", str(path))
    return record


class _Listener(socket.socket):
    """A listening socket that also carries its endpoint token."""
    azki_token = None


def listen(path, backlog=8, *, token=True):
    """Listen on 127.0.0.1:<dynamic> and publish the endpoint at ``path``.

    Returns the listening socket; its ``azki_token`` attribute is the token
    a client must send first (None when ``token`` is False, for QEMU).
    """
    server = _Listener(socket.AF_INET, socket.SOCK_STREAM)
    try:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        server.bind(("127.0.0.1", 0))
        server.listen(backlog)
        server.azki_token = secrets.token_hex(TOKEN_BYTES) if token else None
        write_endpoint(path, server.getsockname()[1], server.azki_token)
    except BaseException:
        server.close()
        raise
    return server


def authenticate(connection, token, timeout=5.0):
    """Read and check the client's first line; closes and raises on mismatch."""
    if token is None:
        return connection
    previous = connection.gettimeout()
    connection.settimeout(timeout)
    try:
        expected = (token + "\n").encode()
        data = b""
        while len(data) < len(expected):
            chunk = connection.recv(len(expected) - len(data))
            if not chunk:
                break
            data += chunk
        if not secrets.compare_digest(data, expected):
            raise PermissionError(errno.EACCES, "Loopback endpoint token rejected")
    except BaseException:
        connection.close()
        raise
    connection.settimeout(previous)
    return connection


def accept(server, timeout=5.0):
    """Accept one authenticated connection (``(connection, address)``)."""
    connection, address = server.accept()
    return authenticate(connection, getattr(server, "azki_token", None), timeout), address


def connect(path, timeout=None, *, retry_seconds=0.0):
    """Connect to the endpoint published at ``path`` and send its token."""
    deadline = time.monotonic() + retry_seconds
    while True:
        record = read_endpoint(path)
        connection = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        if timeout is not None:
            connection.settimeout(timeout)
        try:
            connection.connect((record["host"], record["port"]))
            if record["token"] is not None:
                connection.sendall((record["token"] + "\n").encode())
            return connection
        except (ConnectionRefusedError, ConnectionResetError):
            connection.close()
            if time.monotonic() >= deadline:
                raise
            time.sleep(.02)
        except BaseException:
            connection.close()
            raise


def free_port():
    """A currently unused loopback port for a server that cannot report one (QEMU)."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def peer_identity(connection):
    """(pid, uid) of the process at the other end of a loopback TCP connection.

    The PID comes from the kernel TCP table; uid is 0 (os.getuid()) when that
    process runs as the current user and -1 otherwise.
    """
    import psutil
    local, remote = connection.getsockname()[:2], connection.getpeername()[:2]
    for item in psutil.net_connections(kind="tcp4"):
        if item.laddr and item.raddr and tuple(item.laddr) == tuple(remote) and tuple(item.raddr) == tuple(local):
            try:
                same = psutil.Process(item.pid).username() == psutil.Process().username()
            except psutil.Error:
                same = False
            return item.pid, 0 if same else -1
    raise OSError(errno.ENOTCONN, "Loopback peer not found in the TCP table")


def install():
    """Install the emulation into ``os``, ``signal`` and ``sys.modules`` (idempotent)."""
    global _installed
    if _installed:
        return
    import signal
    for name in ("open", "close", "stat", "lstat", "fstat", "mkdir", "rmdir", "unlink", "remove", "rename",
                 "replace", "link", "readlink", "chmod", "utime", "fsync", "listdir", "scandir", "kill"):
        _native[name] = getattr(os, name)
    _native["abspath"] = os.path.abspath
    os.O_NOFOLLOW, os.O_DIRECTORY, os.O_NONBLOCK, os.O_CLOEXEC = O_NOFOLLOW, O_DIRECTORY, O_NONBLOCK, O_CLOEXEC
    os.open, os.close = _open, _close
    os.stat, os.lstat, os.fstat = _stat, _lstat, _fstat
    os.mkdir, os.rmdir, os.unlink, os.remove = _mkdir, _rmdir, _unlink, _unlink
    os.rename, os.replace, os.link, os.readlink = _rename, _replace, _link, _readlink
    os.chmod, os.fchmod, os.utime, os.fsync = _chmod, _fchmod, _utime, _fsync
    os.listdir, os.scandir = _listdir, _scandir
    os.pread, os.pwrite = _pread, _pwrite
    os.getuid = os.geteuid = os.getgid = os.getegid = _getuid
    os.kill, os.killpg, os.getpgid, os.getpgrp, os.setsid = _kill, _killpg, _getpgid, _getpgrp, setsid
    if not hasattr(signal, "SIGKILL"):
        signal.SIGKILL = 9
    sys.modules.setdefault("fcntl", _fcntl_module())
    sys.modules.setdefault("resource", _resource_module())
    _installed = True
