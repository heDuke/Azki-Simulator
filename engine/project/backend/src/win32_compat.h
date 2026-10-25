/* SPDX-License-Identifier: GPL-2.0-only
 * Windows (MinGW-w64 UCRT / LLVM) equivalents of the POSIX calls used by the
 * yaffs-image, legacy-ui-db and instance-props host helpers. Included only
 * when _WIN32 is defined; POSIX builds never see this file.
 *
 * Every open() is binary and non-inheritable. O_NOFOLLOW rejects a final
 * component that is a reparse point (symbolic link or junction). Paths are
 * in the process code page, exactly as received in argv.
 */
#ifndef AZKI_WIN32_COMPAT_H
#define AZKI_WIN32_COMPAT_H
#ifdef _WIN32
#include <windows.h>
#include <dirent.h>
#include <direct.h>
#include <errno.h>
#include <fcntl.h>
#include <io.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>

#define AZKI_O_NOFOLLOW 0x10000000
#undef O_NOFOLLOW
#define O_NOFOLLOW AZKI_O_NOFOLLOW
#ifndef O_CLOEXEC
#define O_CLOEXEC 0
#endif
#ifndef O_NONBLOCK
#define O_NONBLOCK 0
#endif

static int azki_win32_is_reparse(const char *path)
{
    DWORD attributes = GetFileAttributesA(path);
    return attributes != INVALID_FILE_ATTRIBUTES &&
        (attributes & FILE_ATTRIBUTE_REPARSE_POINT);
}

static int azki_win32_open(const char *path, int flags, ...)
{
    int mode = 0;
    if (flags & O_CREAT) {
        va_list list;
        va_start(list, flags);
        mode = va_arg(list, int);
        va_end(list);
    }
    if ((flags & AZKI_O_NOFOLLOW) && azki_win32_is_reparse(path)) {
        errno = ELOOP;
        return -1;
    }
    flags &= ~AZKI_O_NOFOLLOW;
    return _open(path, flags | _O_BINARY | _O_NOINHERIT,
                 mode ? _S_IREAD | _S_IWRITE : 0);
}
#define open azki_win32_open

/* POSIX ownership: the helpers run as the user owning their private session
 * directory, whose ACL replaces POSIX mode bits. UCRT reports st_uid 0. */
static inline unsigned azki_win32_geteuid(void) { return 0; }
#define geteuid azki_win32_geteuid

/* Exclusive single-link regular file: GetFileInformationByHandle replaces the
 * POSIX st_nlink/st_mode checks, which UCRT cannot report. */
static int azki_win32_private_file(int fd)
{
    BY_HANDLE_FILE_INFORMATION info;
    HANDLE file = (HANDLE)_get_osfhandle(fd);
    return file != INVALID_HANDLE_VALUE && GetFileInformationByHandle(file, &info) &&
        GetFileType(file) == FILE_TYPE_DISK && info.nNumberOfLinks == 1 &&
        !(info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT));
}

static int azki_win32_lstat(const char *path, struct stat *st)
{
    if (stat(path, st)) { return -1; }
    if (azki_win32_is_reparse(path)) {
        st->st_mode = 0; /* neither S_ISREG nor S_ISDIR: rejected like a link */
    }
    return 0;
}
#define lstat azki_win32_lstat

static ssize_t azki_win32_transfer(int fd, void *bytes, size_t count, int64_t offset, int write)
{
    HANDLE file = (HANDLE)_get_osfhandle(fd);
    OVERLAPPED position = { 0 };
    DWORD done = 0, chunk = count > 0x40000000u ? 0x40000000u : (DWORD)count;
    position.Offset = (DWORD)offset;
    position.OffsetHigh = (DWORD)((uint64_t)offset >> 32);
    if (file == INVALID_HANDLE_VALUE) { errno = EBADF; return -1; }
    if (!(write ? WriteFile(file, bytes, chunk, &done, &position)
                : ReadFile(file, bytes, chunk, &done, &position))) {
        if (!write && GetLastError() == ERROR_HANDLE_EOF) { return 0; }
        errno = EIO;
        return -1;
    }
    return done;
}

/* Loops until complete, as callers compare the result with the full count. */
static ssize_t azki_win32_full(int fd, void *bytes, size_t count, int64_t offset, int write)
{
    size_t done = 0;
    while (done < count) {
        ssize_t n = azki_win32_transfer(fd, (char *)bytes + done, count - done, offset + done, write);
        if (n < 0) { return -1; }
        if (!n) { break; }
        done += n;
    }
    return done;
}
#define pread(fd, bytes, count, offset) azki_win32_full((fd), (bytes), (count), (offset), 0)
#define pwrite(fd, bytes, count, offset) azki_win32_full((fd), (void *)(bytes), (count), (offset), 1)
#define fsync _commit

/* Declared by <stdlib.h> only with _CRT_RAND_S, which callers did not set. */
_CRTIMP errno_t __cdecl rand_s(unsigned int *value);

static char *azki_win32_mkdtemp(char *pattern)
{
    size_t length = strlen(pattern);
    if (length < 6 || strcmp(pattern + length - 6, "XXXXXX")) { errno = EINVAL; return NULL; }
    for (int attempt = 0; attempt < 100; attempt++) {
        static const char letters[] = "abcdefghijklmnopqrstuvwxyz0123456789";
        unsigned int value;
        for (int i = 0; i < 6; i++) {
            if (rand_s(&value)) { errno = EIO; return NULL; }
            pattern[length - 6 + i] = letters[value % (sizeof(letters) - 1)];
        }
        if (!_mkdir(pattern)) { return pattern; }
        if (errno != EEXIST) { return NULL; }
    }
    errno = EEXIST;
    return NULL;
}
#define mkdtemp azki_win32_mkdtemp

static int azki_win32_alphasort(const struct dirent **a, const struct dirent **b)
{
    return strcmp((*a)->d_name, (*b)->d_name);
}
#define alphasort azki_win32_alphasort

static int azki_win32_scandir(const char *path, struct dirent ***list, void *filter,
                              int (*compare)(const struct dirent **, const struct dirent **))
{
    DIR *directory = opendir(path);
    struct dirent *entry, **items = NULL;
    size_t count = 0;
    (void)filter;
    if (!directory) { return -1; }
    while ((entry = readdir(directory))) {
        struct dirent **next = realloc(items, (count + 1) * sizeof(*items));
        struct dirent *copy = malloc(sizeof(*entry));
        if (!next || !copy) {
            free(copy); free(next ? next : items); closedir(directory); errno = ENOMEM; return -1;
        }
        items = next;
        memcpy(copy, entry, sizeof(*entry));
        items[count++] = copy;
    }
    closedir(directory);
    if (count > 1) {
        qsort(items, count, sizeof(*items), (int (*)(const void *, const void *))compare);
    }
    *list = items;
    return (int)count;
}
#define scandir azki_win32_scandir
#endif
#endif
