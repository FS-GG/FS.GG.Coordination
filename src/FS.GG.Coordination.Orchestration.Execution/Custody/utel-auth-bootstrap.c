#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

/* Distinct, inert source fixture. It is not the managed S4-A child ABI. */
#define SOURCE_SCHEMA "fsgg.utel.auth-bootstrap.inert/1"
#define SOURCE_PROFILE "linux-x64-utel-auth-inert-source/1"
#define REFERENCE_OPERATION "utel-rel-21/original-tag-authentication-only/2"
#define FIXTURE "FSGG-UTEL-INERT-V1\n"
#define FIXTURE_BYTES (sizeof(FIXTURE) - 1U)
#define FIXTURE_DEADLINE_NS 1000000000LL
#define REFUSED 125

static const char describe_output[] =
    "{\"schema\":\"" SOURCE_SCHEMA "\",\"profile\":\"" SOURCE_PROFILE
    "\",\"referenceOperation\":\"" REFERENCE_OPERATION
    "\",\"architecture\":\"linux-x64\",\"fdRoles\":[0,1,2,3],\"status\":\"source-only\","
    "\"productionAvailable\":false,\"credentialIngressAvailable\":false,"
    "\"sameTokenCleanupAvailable\":false,\"nativeConstructorAvailable\":false,"
    "\"authorized\":false}\n";

static const char fixture_output[] =
    "{\"schema\":\"" SOURCE_SCHEMA "\",\"profile\":\"" SOURCE_PROFILE
    "\",\"referenceOperation\":\"" REFERENCE_OPERATION
    "\",\"architecture\":\"linux-x64\",\"fdRoles\":[0,1,2,3],\"status\":\"inert\","
    "\"productionAvailable\":false,\"credentialIngressAvailable\":false,"
    "\"sameTokenCleanupAvailable\":false,\"nativeConstructorAvailable\":false,"
    "\"authorized\":false}\n";

_Static_assert(FIXTURE_BYTES == 19U, "fixed inert fixture length");
_Static_assert(sizeof(describe_output) <= 512U, "bounded describe output");
_Static_assert(sizeof(fixture_output) <= 512U, "bounded fixture output");

static int64_t now_ns(void) {
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0 || value.tv_sec < 0 || value.tv_nsec < 0
        || value.tv_nsec >= 1000000000L || value.tv_sec > INT64_MAX / 1000000000LL - 2) {
        return -1;
    }
    return (int64_t)value.tv_sec * 1000000000LL + value.tv_nsec;
}

static int wait_fd(int fd, short events, int64_t deadline) {
    for (;;) {
        int64_t current = now_ns();
        if (current < 0 || current >= deadline) return -1;
        int64_t remaining = deadline - current;
        int milliseconds = (int)((remaining + 999999LL) / 1000000LL);
        if (milliseconds < 1) milliseconds = 1;
        struct pollfd observed = {.fd = fd, .events = events, .revents = 0};
        int result = poll(&observed, 1, milliseconds);
        if (result < 0 && errno == EINTR) continue;
        if (result != 1 || (observed.revents & (POLLERR | POLLNVAL)) != 0) return -1;
        if ((observed.revents & (events | POLLHUP)) != 0) {
            int64_t after = now_ns();
            return after >= 0 && after < deadline ? 0 : -1;
        }
    }
}

static int make_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL);
    return flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0 ? -1 : 0;
}

static int no_extra_descriptors(void) {
    DIR *directory = opendir("/proc/self/fd");
    if (directory == NULL) return -1;
    int self = dirfd(directory);
    if (self < 0) { closedir(directory); return -1; }
    int result = 0;
    errno = 0;
    for (;;) {
        struct dirent *entry = readdir(directory);
        if (entry == NULL) {
            if (errno != 0) result = -1;
            break;
        }
        char *end = NULL;
        errno = 0;
        long number = strtol(entry->d_name, &end, 10);
        if (errno != 0 || end == entry->d_name || *end != '\0') {
            if (errno != 0) { result = -1; break; }
            continue;
        }
        if (number >= 4 && number != self) { result = -1; break; }
        errno = 0;
    }
    if (closedir(directory) != 0) result = -1;
    return result;
}

static int read_exact(int fd, unsigned char *buffer, size_t size, int64_t deadline) {
    size_t offset = 0;
    while (offset < size) {
        if (wait_fd(fd, POLLIN, deadline) != 0) return -1;
        ssize_t count = read(fd, buffer + offset, size - offset);
        int64_t after = now_ns();
        if (after < 0 || after >= deadline) return -1;
        if (count > 0) offset += (size_t)count;
        else if (count == 0) return -1;
        else if (errno != EINTR && errno != EAGAIN) return -1;
    }
    return 0;
}

static int exact_eof(int fd, int64_t deadline) {
    unsigned char extra;
    for (;;) {
        if (wait_fd(fd, POLLIN, deadline) != 0) return -1;
        ssize_t count = read(fd, &extra, 1);
        int64_t after = now_ns();
        if (after < 0 || after >= deadline) return -1;
        if (count == 0) return 0;
        if (count > 0 || (errno != EINTR && errno != EAGAIN)) return -1;
    }
}

static int output(const char *text, size_t size, int64_t deadline) {
    struct stat sink;
    if (fstat(STDOUT_FILENO, &sink) != 0 || !S_ISFIFO(sink.st_mode)) return -1;
    if (make_nonblocking(STDOUT_FILENO) != 0 || wait_fd(STDOUT_FILENO, POLLOUT, deadline) != 0) return -1;
    ssize_t count = write(STDOUT_FILENO, text, size);
    int64_t after = now_ns();
    return count == (ssize_t)size && after >= 0 && after < deadline ? 0 : -1;
}

static void interrupted(int signum) { (void)signum; }

int main(int argc, char **argv) {
    if (argc != 2 || argv == NULL || argv[1] == NULL) return REFUSED;
    if (strcmp(argv[1], "--describe") != 0 && strcmp(argv[1], "--inert-fixture") != 0) return REFUSED;
    struct sigaction action = {.sa_handler = interrupted, .sa_flags = 0};
    if (signal(SIGPIPE, SIG_IGN) == SIG_ERR || sigemptyset(&action.sa_mask) != 0
        || sigaction(SIGUSR1, &action, NULL) != 0 || no_extra_descriptors() != 0) return REFUSED;
    int64_t started = now_ns();
    if (started < 0) return REFUSED;
    int64_t deadline = started + FIXTURE_DEADLINE_NS;
    if (strcmp(argv[1], "--describe") == 0) {
        return output(describe_output, sizeof(describe_output) - 1U, deadline) == 0 ? 0 : REFUSED;
    }

    struct stat source;
    if (fstat(3, &source) != 0 || !S_ISFIFO(source.st_mode) || make_nonblocking(3) != 0) return REFUSED;
    unsigned char prefix[4];
    if (read_exact(3, prefix, sizeof(prefix), deadline) != 0
        || prefix[0] != 0 || prefix[1] != 0 || prefix[2] != 0 || prefix[3] != FIXTURE_BYTES) return REFUSED;
    unsigned char body[FIXTURE_BYTES];
    if (read_exact(3, body, sizeof(body), deadline) != 0
        || memcmp(body, FIXTURE, sizeof(body)) != 0 || exact_eof(3, deadline) != 0) return REFUSED;
    return output(fixture_output, sizeof(fixture_output) - 1U, deadline) == 0 ? 0 : REFUSED;
}
