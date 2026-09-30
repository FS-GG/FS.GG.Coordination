from pathlib import Path

from app import greeting

assert greeting() == "hello from portable python"
Path("/output/python-test.json").write_bytes(
    b'{"outcome":"passed","verification":"python-test-v1"}\n'
)
print("python-test-ok")
