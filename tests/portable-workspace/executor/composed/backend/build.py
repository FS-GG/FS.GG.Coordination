import py_compile
from pathlib import Path

Path("artifact").mkdir(exist_ok=True)
py_compile.compile("service.py", cfile="artifact/service.pyc", doraise=True)
print("backend-build-ok")
