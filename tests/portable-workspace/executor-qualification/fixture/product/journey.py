import pathlib
import socket
import subprocess
import sys
import time

root = pathlib.Path(__file__).resolve().parents[1]
built_javascript = pathlib.Path("/output/frontend/app.js")
built_javascript.parent.mkdir(parents=True, exist_ok=True)
subprocess.run(
    ["/opt/typescript/bin/tsc", "--project", str(root / "frontend" / "tsconfig.json"), "--outDir", str(built_javascript.parent)],
    check=True,
)

backend = root / "backend" / "service.py"
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
        ["/usr/local/bin/node", built_javascript, f"http://127.0.0.1:{port}/message"],
        check=True,
    )
    pathlib.Path("/output/composed-journey.json").write_bytes(
        b'{"outcome":"passed","verification":"composed-journey-v1"}\n'
    )
finally:
    server.terminate()
    server.wait(timeout=5)
