import pathlib
import socket
import subprocess
import sys
import time

built_javascript = pathlib.Path(sys.argv[1])
absolute_node = pathlib.Path(sys.argv[2])
if not built_javascript.is_absolute() or not absolute_node.is_absolute():
    raise SystemExit("journey requires absolute built-JavaScript and Node paths")

backend = pathlib.Path(__file__).resolve().parents[1] / "backend" / "service.py"
probe = socket.socket()
probe.bind(("127.0.0.1", 0))
port = probe.getsockname()[1]
probe.close()

server = subprocess.Popen(
    [sys.executable, "-c", f"from service import serve; serve({port})"],
    cwd=backend.parent,
)
try:
    deadline = time.monotonic() + 5
    while True:
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.1):
                break
        except OSError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.05)
    subprocess.run(
        [absolute_node, built_javascript, f"http://127.0.0.1:{port}/message"],
        check=True,
    )
    pathlib.Path("/output/composed-journey.json").write_bytes(
        b'{"outcome":"passed","verification":"composed-journey-v1"}\n'
    )
finally:
    server.terminate()
    server.wait(timeout=5)
