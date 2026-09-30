import pathlib
import socket
import subprocess
import sys
import time

root = pathlib.Path(__file__).resolve().parents[1]
server_script = root / "backend" / "service.py"
frontend_script = root / "frontend" / "journey.mjs"

probe = socket.socket()
probe.bind(("127.0.0.1", 0))
port = probe.getsockname()[1]
probe.close()

server_code = server_script.read_text().replace("HTTPServer((\"127.0.0.1\", 0)", f"HTTPServer((\"127.0.0.1\", {port})")
server = subprocess.Popen([sys.executable, "-c", server_code], cwd=server_script.parent)

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
        ["node", str(frontend_script), f"http://127.0.0.1:{port}/message"],
        cwd=frontend_script.parent,
        check=True,
    )
finally:
    server.terminate()
    server.wait(timeout=5)
