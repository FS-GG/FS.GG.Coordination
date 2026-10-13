"""Finite .2-C fixtures; entry must receive its single-byte release within one second.
Inherited writers self-expire, and no fixture sends signals or starts a new session.
"""
import os
import select
import sys
import threading
import time

if len(sys.argv) < 2:
    raise ValueError("missing case")
case = sys.argv[1]
if not select.select([0], [], [], 1.0)[0] or os.read(0, 1) != b"G":
    sys.exit(64)

if case == "literal-normal":
    if sys.argv[2:] != ["literal space", 'quote"and\\slash', "café"] or os.environ.get("FSGG_LITERAL") != "literal env é":
        sys.exit(70)
    os.write(1, b"argv/env-ok\n" + bytes(range(256)))
    os.write(2, bytes(reversed(range(256))))
elif case in ("ordinary-dual", "ordinary-overflow"):
    size = 256 if case == "ordinary-dual" else 32768
    def write(fd, byte):
        try:
            for _ in range(size // 256):
                os.write(fd, byte * 256)
        except BrokenPipeError:
            pass
    workers = [threading.Thread(target=write, args=(1, b"O")), threading.Thread(target=write, args=(2, b"E"))]
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join()
elif case in ("text-large-stdout", "text-large-stderr", "text-large-mixed", "text-exact", "text-over"):
    counts = {"text-large-stdout": (300000, 0), "text-large-stderr": (0, 300000),
              "text-large-mixed": (150000, 150000), "text-exact": (200000, 200000),
              "text-over": (200001, 200000)}[case]
    def emit(fd, count, value):
        try:
            while count:
                block = value * min(count, 1024)
                written = os.write(fd, block)
                count -= written
        except BrokenPipeError:
            pass
    workers = [threading.Thread(target=emit, args=(1, counts[0], b"O")),
               threading.Thread(target=emit, args=(2, counts[1], b"E"))]
    for worker in workers:
        worker.start()
    for worker in workers:
        worker.join()
    if case.startswith("text-large"):
        emit(1, 1, b"1")
        os.write(1, b" passing\n")
elif case == "text-surrogate":
    payload = b"A" * 1023 + "😀".encode("utf-8")
    if os.write(1, payload) != len(payload):
        raise ValueError("short atomic fixture write")
elif case == "text-encoding":
    mode = sys.argv[2]
    value = "café😀\n"
    payloads = {"utf8": value.encode("utf-8"), "utf8-bom": b"\xef\xbb\xbf" + value.encode("utf-8"),
                "utf16le": b"\xff\xfe" + value.encode("utf-16-le"),
                "utf16be": b"\xfe\xff" + value.encode("utf-16-be"),
                "utf32le": b"\xff\xfe\x00\x00" + value.encode("utf-32-le"),
                "utf32be": b"\x00\x00\xfe\xff" + value.encode("utf-32-be"),
                "malformed": b"A\xffB\xe2\x82", "utf32-large": b"\xff\xfe\x00\x00" + ("X" * 300000).encode("utf-32-le")}
    payload = payloads[mode]
    while payload:
        written = os.write(1, payload[:1024])
        payload = payload[written:]
elif case in ("ordinary-deadline", "ordinary-cancel"):
    os.write(1, b"started\n")
    time.sleep(1.5)
elif case == "ordinary-heldpipe":
    if os.fork() == 0:
        time.sleep(1.5)
        os._exit(0)
    os.write(1, b"leader-exiting\n")
else:
    raise ValueError(case)
