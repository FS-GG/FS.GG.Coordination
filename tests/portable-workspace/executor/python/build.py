import os
import pathlib
import py_compile

target = pathlib.Path(os.environ.get("PORTABLE_OUTPUT_ROOT", "/output")) / "python/app.pyc"
target.parent.mkdir(parents=True, exist_ok=True)
py_compile.compile(
    "app.py",
    cfile=target,
    dfile="/source/tests/portable-workspace/image/fixture/python/app.py",
    doraise=True,
    invalidation_mode=py_compile.PycInvalidationMode.CHECKED_HASH,
)
print("python-build-ok")
