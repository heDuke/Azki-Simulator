/* SPDX-License-Identifier: GPL-2.0-or-later
 * Windows host equivalents for the private NAND/NOR working-copy stores and
 * the shared LCD frame. Included only when _WIN32 is defined; POSIX hosts
 * keep using open/fstat/flock/pread/pwrite/mmap unchanged.
 *
 * Store files are opened without following reparse points, with no sharing
 * (the equivalent of the exclusive non-blocking flock on POSIX), and must be
 * single-link regular files owned by the current user of the exact size.
 * The per-user session directory ACL replaces the POSIX 0600 mode check.
 * Paths are in the process code page, like every other QEMU argv path.
 */
#ifndef BES_HOST_WIN32_H
#define BES_HOST_WIN32_H
#ifdef _WIN32
#include <errno.h>
#include <io.h>
#include <fcntl.h>
#include <stdbool.h>
#include <stdint.h>
#include <windows.h>
#include <aclapi.h>

static bool bes_win32_owned_by_current_user(HANDLE file)
{
    PSID owner = NULL;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    HANDLE token = NULL;
    bool owned = false;
    if (GetSecurityInfo(file, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION,
                        &owner, NULL, NULL, NULL, &descriptor) != ERROR_SUCCESS) {
        return false;
    }
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        union { TOKEN_USER user; TOKEN_OWNER owner; BYTE bytes[256]; } info;
        DWORD length = 0;
        if (GetTokenInformation(token, TokenUser, &info, sizeof(info), &length) &&
            EqualSid(owner, info.user.User.Sid)) {
            owned = true;
        } else if (GetTokenInformation(token, TokenOwner, &info, sizeof(info), &length) &&
                   EqualSid(owner, info.owner.Owner)) {
            /* Elevated tokens create files owned by their default owner. */
            owned = true;
        }
        CloseHandle(token);
    }
    LocalFree(descriptor);
    return owned;
}

static bool bes_win32_handle_valid(HANDLE file, uint64_t bytes)
{
    BY_HANDLE_FILE_INFORMATION info;
    if (file == INVALID_HANDLE_VALUE || !GetFileInformationByHandle(file, &info) ||
        GetFileType(file) != FILE_TYPE_DISK ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT |
                                  FILE_ATTRIBUTE_DEVICE)) ||
        info.nNumberOfLinks != 1 ||
        ((uint64_t)info.nFileSizeHigh << 32 | info.nFileSizeLow) != bytes ||
        !bes_win32_owned_by_current_user(file)) {
        errno = EINVAL;
        return false;
    }
    return true;
}

static bool bes_win32_fd_valid(int fd, uint64_t bytes)
{
    return bes_win32_handle_valid((HANDLE)_get_osfhandle(fd), bytes);
}

/* Exclusive (unshared) read/write open of an existing private store file. */
static int bes_win32_open_store(const char *path, uint64_t bytes)
{
    HANDLE file = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL,
                              OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
                              NULL);
    if (file == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        errno = (error == ERROR_SHARING_VIOLATION || error == ERROR_LOCK_VIOLATION)
            ? EWOULDBLOCK : (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
            ? ENOENT : EACCES;
        return -1;
    }
    if (!bes_win32_handle_valid(file, bytes)) {
        CloseHandle(file);
        errno = EINVAL;
        return -1;
    }
    int fd = _open_osfhandle((intptr_t)file, _O_RDWR | _O_BINARY);
    if (fd < 0) { CloseHandle(file); errno = EMFILE; return -1; }
    return fd;
}

static ssize_t bes_win32_transfer(int fd, void *bytes, size_t count,
                                  uint64_t offset, bool write)
{
    HANDLE file = (HANDLE)_get_osfhandle(fd);
    OVERLAPPED position = { 0 };
    DWORD done = 0;
    DWORD chunk = count > 0x40000000u ? 0x40000000u : (DWORD)count;
    position.Offset = (DWORD)offset;
    position.OffsetHigh = (DWORD)(offset >> 32);
    if (file == INVALID_HANDLE_VALUE) { errno = EBADF; return -1; }
    BOOL ok = write ? WriteFile(file, bytes, chunk, &done, &position)
                    : ReadFile(file, bytes, chunk, &done, &position);
    if (!ok) {
        if (!write && GetLastError() == ERROR_HANDLE_EOF) { return 0; }
        errno = EIO;
        return -1;
    }
    return done;
}

static inline ssize_t pread(int fd, void *bytes, size_t count, off_t offset)
{
    return bes_win32_transfer(fd, bytes, count, (uint64_t)offset, false);
}

static inline ssize_t pwrite(int fd, const void *bytes, size_t count, off_t offset)
{
    return bes_win32_transfer(fd, (void *)bytes, count, (uint64_t)offset, true);
}

/* Shared LCD frame: a file-backed mapping that readers may open and that the
 * host may rename or delete while mapped. Returns NULL with errno set. */
static void *bes_win32_map_frame(const char *path, size_t bytes)
{
    HANDLE file = CreateFileA(path, GENERIC_READ | GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) { errno = EACCES; return NULL; }
    HANDLE mapping = CreateFileMappingW(file, NULL, PAGE_READWRITE,
                                        (DWORD)((uint64_t)bytes >> 32),
                                        (DWORD)bytes, NULL);
    void *view = mapping ? MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, bytes) : NULL;
    if (mapping) { CloseHandle(mapping); }
    CloseHandle(file);
    if (!view) { errno = ENOMEM; }
    return view;
}

static void bes_win32_unmap_frame(void *view)
{
    if (view) {
        FlushViewOfFile(view, 0);
        UnmapViewOfFile(view);
    }
}
#endif
#endif
