import py_compile
from pathlib import Path

Path("artifact").mkdir(exist_ok=True)
py_compile.compile("app.py", cfile="artifact/app.pyc", doraise=True)
print("python-build-ok")
