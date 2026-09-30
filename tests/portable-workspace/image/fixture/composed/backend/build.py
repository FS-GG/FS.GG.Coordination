import pathlib
import py_compile

target = pathlib.Path("/output/backend/service.pyc")
target.parent.mkdir(parents=True, exist_ok=True)
py_compile.compile(
    "service.py",
    cfile=target,
    dfile="/source/tests/portable-workspace/image/fixture/composed/backend/service.py",
    doraise=True,
    invalidation_mode=py_compile.PycInvalidationMode.CHECKED_HASH,
)
