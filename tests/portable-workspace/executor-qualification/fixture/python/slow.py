import pathlib
import time

time.sleep(10)
pathlib.Path("/output/slow.json").write_bytes(
    b'{"outcome":"passed","verification":"slow-v1"}\n'
)
