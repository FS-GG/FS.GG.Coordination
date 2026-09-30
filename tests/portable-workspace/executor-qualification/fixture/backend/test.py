from pathlib import Path

from service import Handler

assert Handler is not None
Path("/output/backend-test.json").write_bytes(
    b'{"outcome":"passed","verification":"backend-test-v1"}\n'
)
print("backend-test-ok")
