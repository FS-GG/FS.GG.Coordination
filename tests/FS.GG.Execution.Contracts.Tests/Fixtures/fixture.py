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
