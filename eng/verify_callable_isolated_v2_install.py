"""Read-only, source-only pin check for the inspect-only isolated v2 zipapp.

The exact archive is the #555 candidate. The #559 authority module must exist
in source but is deliberately absent from that archive. This module has no CLI,
credential, issuer, journal, provider, or dispatch adapter. Interpreter pins
must originate from a future protected release decision, not its caller.
"""

from __future__ import annotations

import dataclasses
import hashlib
import io
import json
import os
import pathlib
import re
import stat
import subprocess
import tempfile
import zipfile
from typing import Any

MANIFEST_SCHEMA = "fsgg.coordination.callable-isolated-v2-zipapp-manifest/1"
INTERPRETER_SCHEMA = "fsgg.coordination.callable-isolated-v2-interpreter-pin/1"
ARCHIVE_NAME = "fsgg-callable-isolated-v2.pyz"
ARCHIVE_SHA256 = "003f63ac1b0f895642a7000954607b0980f8e9f1dbb058576e66b40ed79cbfb7"
ARCHIVE_SIZE = 14_491
MANIFEST_SHA256 = "9cf485b238a458c3234adafb6f3964b7e105507a8d8cfbeb01a1d067b2545fd3"
BUILDER_SHA256 = "2c581bed909f835ae24de9b79ab1e2a1d4b144dd5dcf3b8b3285872c4af6ed6b"
AUTHORITY_SOURCE_SHA256 = "9a806bb78de25be887db0a0b9499f242d7989d33860e598e23e2ceff80172a26"
MEMBERS = (
    ("__main__.py", "eng/callable_isolated_v2_entry.py",
     "03b0f43fffb265e1895e32ef76239e5076bbd22d4da690383b7479f915ab9adc", 3488),
    ("callable_isolated_v2_grant.py", "eng/callable_isolated_v2_grant.py",
     "f37feb7dd835f3327fb1aac0987d3397ea19314cdfcf42099a0c490b48220da4", 10749),
)
FIXED_TIME = (1980, 1, 1, 0, 0, 0)
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
VERSION = re.compile(r"[0-9]+\.[0-9]+\.[0-9]+\Z")
USAGE = "usage: inspect-grant --grant FILE --digest SHA256 --expected FILE --replay FILE\n"


class Refused(ValueError):
    """Fixed refusal code; no raw path, token, or subprocess output."""


@dataclasses.dataclass(frozen=True)
class InstallEvidence:
    archive_sha256: str
    interpreter_sha256: str
    interpreter_version: str
    member_sha256: tuple[str, str]
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise Refused("install-duplicate-member")
        result[key] = value
    return result


def _regular(path: pathlib.Path, limit: int) -> bytes:
    if not isinstance(path, pathlib.Path) or not path.is_absolute():
        raise Refused("install-path")
    try:
        if path.resolve(strict=True) != path:
            raise Refused("install-path")
        metadata = path.lstat()
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > limit:
            raise Refused("install-file")
        raw = path.read_bytes()
    except Refused:
        raise
    except (OSError, RuntimeError):
        raise Refused("install-file") from None
    if len(raw) > limit or len(raw) != metadata.st_size:
        raise Refused("install-file")
    return raw


def _manifest(root: pathlib.Path) -> dict[str, Any]:
    raw = _regular(root / "work/gs2-09-9-isolated-operator-rotation/zipapp-manifest.json", 4096)
    if _sha(raw) != MANIFEST_SHA256:
        raise Refused("install-manifest-digest")
    try:
        value = json.loads(raw.decode("ascii"), object_pairs_hook=_unique)
    except (UnicodeError, ValueError):
        raise Refused("install-manifest-json") from None
    expected = {
        "schema": MANIFEST_SCHEMA, "artifact": ARCHIVE_NAME,
        "archiveSha256": ARCHIVE_SHA256, "archiveSize": ARCHIVE_SIZE,
        "builderSourceSha256": BUILDER_SHA256,
        "buildContract": "python3-stdlib-zipfile-stored-fixed-metadata",
        "entry": ["python3", "-I", ARCHIVE_NAME, "inspect-grant"],
        "members": [{"path": path, "source": source, "sha256": digest, "size": size}
                    for path, source, digest, size in MEMBERS],
        "state": "inspect-only-not-installed",
    }
    if value != expected:
        raise Refused("install-manifest-binding")
    return value


def _verify_members(raw: bytes) -> tuple[str, str]:
    """Verify strict ZIP layout independently of the outer archive digest."""
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            infos = archive.infolist()
            if len(infos) != len(MEMBERS) or [i.filename for i in infos] != [m[0] for m in MEMBERS]:
                raise Refused("install-member-set")
            result = []
            for info, (name, _source, digest, size) in zip(infos, MEMBERS):
                if (info.filename != name or info.file_size != size
                        or info.compress_type != zipfile.ZIP_STORED
                        or info.date_time != FIXED_TIME
                        or info.external_attr >> 16 != 0o100644
                        or info.flag_bits & 0x1 or info.is_dir()):
                    raise Refused("install-member-metadata")
                body = archive.read(info)
                if len(body) != size or _sha(body) != digest:
                    raise Refused("install-member-digest")
                result.append(digest)
            if archive.comment:
                raise Refused("install-archive-comment")
            return result[0], result[1]
    except Refused:
        raise
    except (OSError, ValueError, RuntimeError, zipfile.BadZipFile, NotImplementedError):
        raise Refused("install-archive-invalid") from None


def _interpreter_pin(value: Any) -> tuple[pathlib.Path, str, str]:
    if type(value) is not dict or set(value) != {"schema", "path", "sha256", "version",
                                                   "implementation", "flags"}:
        raise Refused("install-interpreter-pin-shape")
    if (value["schema"] != INTERPRETER_SCHEMA
            or type(value["path"]) is not str or not value["path"].startswith("/")
            or type(value["sha256"]) is not str or HEX64.fullmatch(value["sha256"]) is None
            or type(value["version"]) is not str or VERSION.fullmatch(value["version"]) is None
            or value["implementation"] != "cpython" or value["flags"] != ["-I", "-S"]):
        raise Refused("install-interpreter-pin")
    return pathlib.Path(value["path"]), value["sha256"], value["version"]


def _run_clean(binary: pathlib.Path, arguments: list[str], cwd: pathlib.Path) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run([str(binary), "-I", "-S", *arguments], cwd=cwd,
                                env={"LC_ALL": "C", "PATH": "/usr/bin:/bin"},
                                capture_output=True, text=True, timeout=10, check=False)
    except (OSError, subprocess.TimeoutExpired, UnicodeError):
        raise Refused("install-interpreter-unavailable") from None
    if len(result.stdout) > 4096 or len(result.stderr) > 4096:
        raise Refused("install-interpreter-output")
    return result


def verify_clean_install(root: pathlib.Path, installed: pathlib.Path,
                         interpreter_pin: dict[str, Any]) -> InstallEvidence:
    """Verify exact candidate bytes, then execute only closed local probes.

    A caller-supplied pin is not trusted authority. A future protected workflow
    must bind it to an independently reviewed runner image and release packet.
    """
    if not isinstance(root, pathlib.Path) or not root.is_absolute():
        raise Refused("install-source-root")
    _manifest(root)
    if _sha(_regular(root / "eng/build_callable_isolated_v2_zipapp.py", 128_000)) != BUILDER_SHA256:
        raise Refused("install-builder-digest")
    for _name, source, digest, _size in MEMBERS:
        if _sha(_regular(root / source, 128_000)) != digest:
            raise Refused("install-source-digest")
    if _sha(_regular(root / "eng/callable_isolated_v2_authority.py", 128_000)) != AUTHORITY_SOURCE_SHA256:
        raise Refused("install-authority-source-digest")
    if not isinstance(installed, pathlib.Path) or not installed.is_absolute():
        raise Refused("install-path")
    if installed.name != ARCHIVE_NAME:
        raise Refused("install-archive-name")
    try:
        if (not installed.parent.is_dir() or installed.parent.is_symlink()
                or set(installed.parent.iterdir()) != {installed}):
            raise Refused("install-directory-not-clean")
    except OSError:
        raise Refused("install-directory-unavailable") from None
    archive = _regular(installed, 32_768)
    if len(archive) != ARCHIVE_SIZE or _sha(archive) != ARCHIVE_SHA256:
        raise Refused("install-archive-digest")
    members = _verify_members(archive)
    binary, binary_digest, version = _interpreter_pin(interpreter_pin)
    binary_raw = _regular(binary, 100_000_000)
    if not os.access(binary, os.X_OK) or _sha(binary_raw) != binary_digest:
        raise Refused("install-interpreter-digest")
    with tempfile.TemporaryDirectory(prefix="fsgg-v2-install-probe-") as temporary:
        cwd = pathlib.Path(temporary)
        probe = _run_clean(binary, ["-c", "import json,sys;print(json.dumps({"
                                    "'version':'.'.join(map(str,sys.version_info[:3])),"
                                    "'implementation':sys.implementation.name,"
                                    "'executable':sys.executable,"
                                    "'isolated':sys.flags.isolated,"
                                    "'no_site':sys.flags.no_site},sort_keys=True))"], cwd)
        try:
            observed = json.loads(probe.stdout, object_pairs_hook=_unique)
        except (ValueError, UnicodeError):
            raise Refused("install-interpreter-runtime") from None
        if (probe.returncode != 0 or probe.stderr or type(observed) is not dict
                or set(observed) != {"version", "implementation", "executable", "isolated", "no_site"}
                or type(observed["isolated"]) is not int
                or type(observed["no_site"]) is not int
                or observed != {"version": version, "implementation": "cpython",
                                "executable": str(binary), "isolated": 1, "no_site": 1}):
            raise Refused("install-interpreter-runtime")
        help_result = _run_clean(binary, [str(installed), "--help"], cwd)
        closed = _run_clean(binary, [str(installed), "execute-native-pull"], cwd)
        if (help_result.returncode != 0 or help_result.stdout != USAGE or help_result.stderr
                or closed.returncode != 2 or closed.stdout or closed.stderr != "inspect-only\n"
                or list(cwd.iterdir())):
            raise Refused("install-entry-not-closed")
    if (_sha(_regular(installed, 32_768)) != ARCHIVE_SHA256
            or _sha(_regular(binary, 100_000_000)) != binary_digest
            or set(installed.parent.iterdir()) != {installed}):
        raise Refused("install-postprobe-drift")
    return InstallEvidence(ARCHIVE_SHA256, binary_digest, version, members)
