"""Fresh ordinary-v1 fixture bytes, unrelated to historical A1-A4.
All children naturally finish within 1.5 seconds, including held writers.
"""
import os
import json
from pathlib import Path
import sys
import threading
import time

def register():
    directory = os.environ.get("FSGG_PROC_REGISTRY")
    if directory:
        stat = Path("/proc/self/stat").read_text().rsplit(")", 1)[1].split()
        Path(directory, str(os.getpid()) + ".json").write_text(json.dumps(
            {"pid": os.getpid(), "startTicks": stat[19]}))

register()
case = sys.argv[1]
if case in ("ordinary-dual", "ordinary-overflow"):
    size = 256 if case == "ordinary-dual" else 32768
    def write(fd, byte):
        try:
            for _ in range(size // 256):
                os.write(fd, byte * 256)
        except BrokenPipeError:
            pass
    streams = [threading.Thread(target=write, args=(1, b'O')),
               threading.Thread(target=write, args=(2, b'E'))]
    for stream in streams:
        stream.start()
    for stream in streams:
        stream.join()
elif case in ("ordinary-deadline", "ordinary-cancel"):
    os.write(1, b"started\n")
    time.sleep(1.5)
elif case == "ordinary-heldpipe":
    # No arbitrary PID signals: inherited writer has a fixed self-expiry.
    if os.fork() == 0:
        register()
        time.sleep(1.5)
        os._exit(0)
    os.write(1, b"leader-exiting\n")
else:
    raise ValueError(case)
