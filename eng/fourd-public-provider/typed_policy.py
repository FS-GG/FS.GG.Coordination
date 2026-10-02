"""Thin bounded process adapter for the qualification-owned F# policy."""

from __future__ import annotations

import hashlib
import json
import os
import datetime as dt
import time
import pathlib
from typing import Mapping

REQUEST_SCHEMA = "fsgg.fourd.typed-operation-request/1"
STATE_SCHEMA = "fsgg.fourd.typed-operation-state/1"
MAX_POLICY_BYTES = 64 * 1024


class TypedPolicyRefusal(RuntimeError):
    pass


def identity(admission: Mapping[str, object]) -> str:
    raw = json.dumps(admission, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()
    return hashlib.sha256(raw).hexdigest()


def project_failure(*, runner, source_root: pathlib.Path, work: pathlib.Path, name: str,
                    callsite: str, category: str, token: str | None,
                    cleanup_complete: bool) -> dict[str, object]:
    request = {"schema":"fsgg.fourd.failure-diagnostic-request/1", "callsite":callsite,
               "category":category, "token":token, "cleanupComplete":cleanup_complete}
    raw = json.dumps(request, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"
    if len(raw) > MAX_POLICY_BYTES:
        raise TypedPolicyRefusal("typed-failure-input-refused")
    input_path, output_path = work / f"failure-{name}.request.json", work / f"failure-{name}.result.json"
    input_path.write_bytes(raw); os.chmod(input_path, 0o600)
    result = runner.run([str(executable(source_root)), "project-failure", str(input_path), str(output_path)],
                        cwd=source_root, env={"PATH":"/usr/bin:/bin", "LANG":"C.UTF-8"}, timeout=30,
                        capture=work / f"failure-{name}.process.json", allow_cancelled=True)
    if result.returncode != 0:
        raise TypedPolicyRefusal("typed-failure-projection-refused")
    try:
        encoded = output_path.read_bytes(); value = json.loads(encoded)
    except (OSError, json.JSONDecodeError) as error:
        raise TypedPolicyRefusal("typed-failure-projection-refused") from error
    expected = {"schema","accepted","ready","failureCode","cleanupComplete"}
    if (len(encoded) > MAX_POLICY_BYTES or not isinstance(value, dict) or set(value) != expected
            or value.get("schema") != "fsgg.fourd.failure-diagnostic/1" or value.get("accepted") is not True
            or type(value.get("ready")) is not bool or type(value.get("cleanupComplete")) is not bool
            or (value.get("failureCode") is not None and not isinstance(value.get("failureCode"), str))):
        raise TypedPolicyRefusal("typed-failure-projection-refused")
    return value


def executable(source_root: pathlib.Path) -> pathlib.Path:
    configured = os.environ.get("FSGG_FOURD_TYPED_POLICY", "")
    value = pathlib.Path(configured) if configured else source_root / "eng/fourd-public-provider/typed/publish/FourD.Typed"
    if not value.is_absolute():
        raise TypedPolicyRefusal("typed-policy-path-refused")
    try:
        current = value.resolve(strict=True)
    except OSError as error:
        raise TypedPolicyRefusal("typed-policy-missing") from error
    if current != value or not value.is_file() or value.stat().st_mode & 0o002:
        raise TypedPolicyRefusal("typed-policy-boundary-refused")
    binding_path = pathlib.Path(os.environ.get("FSGG_FOURD_TYPED_POLICY_BINDING", str(value.parent / "typed-policy-binding.json")))
    try:
        binding = json.loads(binding_path.read_bytes())
    except (OSError, json.JSONDecodeError) as error:
        raise TypedPolicyRefusal("typed-policy-binding-refused") from error
    names = ("FourD.Typed", "FourD.Typed.dll", "FourD.Typed.deps.json", "FourD.Typed.runtimeconfig.json", "FSharp.Core.dll")
    expected = {name: hashlib.sha256((value.parent / name).read_bytes()).hexdigest() for name in names}
    if (not isinstance(binding, dict) or set(binding) != {"schema","sourceSha","files"}
            or binding.get("schema") != "fsgg.fourd.typed-policy-binding/1"
            or binding.get("sourceSha") != os.environ.get("GITHUB_SHA", binding.get("sourceSha"))
            or binding.get("files") != expected):
        raise TypedPolicyRefusal("typed-policy-binding-refused")
    return value


def transition(*, runner, source_root: pathlib.Path, work: pathlib.Path, name: str,
               observation: Mapping[str, object], state: Mapping[str, object] | None = None,
               budget: int = 900) -> dict[str, object]:
    if not name.isascii() or not name.replace("-", "").isalnum() or len(name) > 48:
        raise TypedPolicyRefusal("typed-policy-name-refused")
    observed = dict(observation)
    clock_path = work / "typed-policy-clock.json"
    now = time.monotonic_ns()
    previous = now
    if clock_path.exists():
        try:
            clock = json.loads(clock_path.read_bytes())
            previous = int(clock["monotonicNs"])
        except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
            raise TypedPolicyRefusal("typed-policy-clock-refused") from error
    if previous > now:
        raise TypedPolicyRefusal("typed-policy-clock-refused")
    observed["cost"] = max(1, (now - previous + 999_999_999) // 1_000_000_000)
    request = {"schema": REQUEST_SCHEMA, "observation": observed}
    if state is None:
        request["budget"] = budget
    else:
        request["state"] = dict(state)
    encoded = json.dumps(request, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"
    if len(encoded) > MAX_POLICY_BYTES:
        raise TypedPolicyRefusal("typed-policy-input-refused")
    input_path, output_path = work / f"typed-{name}.request.json", work / f"typed-{name}.result.json"
    input_path.write_bytes(encoded)
    os.chmod(input_path, 0o600)
    result = runner.run([str(executable(source_root)), "transition", str(input_path), str(output_path)],
                        cwd=source_root, env={"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8"}, timeout=30,
                        capture=work / f"typed-{name}.process.json", allow_cancelled=True)
    if result.returncode != 0:
        raise TypedPolicyRefusal("typed-policy-refused")
    try:
        raw = output_path.read_bytes()
        value = json.loads(raw)
    except (OSError, json.JSONDecodeError) as error:
        raise TypedPolicyRefusal("typed-policy-output-refused") from error
    if len(raw) > MAX_POLICY_BYTES or not isinstance(value, dict) or value.get("schema") != STATE_SCHEMA:
        raise TypedPolicyRefusal("typed-policy-output-refused")
    clock_tmp = work / f"typed-policy-clock-{name}.new"
    clock_tmp.write_bytes(json.dumps({"monotonicNs":now}, separators=(",", ":")).encode()+b"\n")
    os.chmod(clock_tmp, 0o600); os.replace(clock_tmp, clock_path)
    return value


def validate_join(*, runner, source_root: pathlib.Path, work: pathlib.Path,
                  admission: Mapping[str, object], placement_sha: str, run_id: str, run_attempt: str,
                  source_sha: str, source_tree: str, inventory_sha256: str, observed_now: str | None = None) -> str:
    request = {"schema":"fsgg.fourd.typed-source-join-request/1", "admission":dict(admission),
               "placementSha":placement_sha, "runId":run_id, "runAttempt":run_attempt,
               "observedSourceSha":source_sha, "observedSourceTree":source_tree,
               "observedInventorySha256":inventory_sha256,
               "observedNow":observed_now or dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00","Z")}
    raw = json.dumps(request, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode() + b"\n"
    input_path, output_path = work / "typed-source-join.request.json", work / "typed-source-join.result.json"
    input_path.write_bytes(raw); os.chmod(input_path, 0o600)
    result = runner.run([str(executable(source_root)), "validate-join", str(input_path), str(output_path)],
                        cwd=source_root, env={"PATH":"/usr/bin:/bin", "LANG":"C.UTF-8"}, timeout=30,
                        capture=work / "typed-source-join.process.json")
    if result.returncode != 0: raise TypedPolicyRefusal("typed-source-join-refused")
    value = json.loads(output_path.read_bytes())
    joined = value.get("identity") if isinstance(value, dict) else None
    if value.get("schema") != "fsgg.fourd.typed-source-join/1" or value.get("accepted") is not True \
            or not isinstance(joined, str) or len(joined) != 64:
        raise TypedPolicyRefusal("typed-source-join-refused")
    return joined


def admit(*, runner, source_root: pathlib.Path, work: pathlib.Path,
          admission: Mapping[str, object], placement_sha: str, run_id: str, run_attempt: str,
          source_sha: str, source_tree: str, inventory_sha256: str, observed_now: str | None = None) -> dict[str, object]:
    value = validate_join(runner=runner, source_root=source_root, work=work, admission=admission,
                          placement_sha=placement_sha, run_id=run_id, run_attempt=run_attempt,
                          source_sha=source_sha, source_tree=source_tree, inventory_sha256=inventory_sha256,
                          observed_now=observed_now)
    state = transition(runner=runner, source_root=source_root, work=work, name="acquire",
                       observation={"kind": "acquire", "identity": value,
                                    "resource": "source-join", "cost": 1})
    state = transition(runner=runner, source_root=source_root, work=work, name="validate",
                       observation={"kind": "validate", "identity": value, "cost": 1}, state=state)
    state = transition(runner=runner, source_root=source_root, work=work, name="admit",
                       observation={"kind": "admit", "identity": value, "cost": 1}, state=state)
    if state.get("effectEligible") is not True:
        raise TypedPolicyRefusal("typed-policy-admission-refused")
    return state
