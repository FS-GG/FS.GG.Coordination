import os
from pathlib import Path
import subprocess
import sys

from app import greeting

assert greeting() == "hello from portable python"
environment = {
    "HOME": os.environ.get("HOME", "/tmp"),
    "PATH": "/usr/local/bin:/usr/bin:/bin",
}
output_root = Path(os.environ.get("PORTABLE_OUTPUT_ROOT", "/output"))
greeting_process = subprocess.run(
    [sys.executable, "app.py"],
    check=True,
    capture_output=True,
    cwd=Path(__file__).parent,
    env=environment,
    text=True,
    timeout=10,
)
assert greeting_process.stdout == "hello from portable python\n"
assert greeting_process.stderr == ""
build_environment = {**environment, "PORTABLE_OUTPUT_ROOT": str(output_root)}
subprocess.run(
    [sys.executable, "build.py"],
    check=True,
    cwd=Path(__file__).parent,
    env=build_environment,
    timeout=10,
)
assert (output_root / "python/app.pyc").is_file()
output_root.mkdir(parents=True, exist_ok=True)
(output_root / "python-test.json").write_bytes(
    b'{"outcome":"passed","verification":"python-test-v1"}\n'
)
print("python-test-ok")
