"""Thin bounded process adapter for the qualification-owned F# policy."""

from __future__ import annotations

import hashlib
import json
import os
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
    return value


def transition(*, runner, source_root: pathlib.Path, work: pathlib.Path, name: str,
               observation: Mapping[str, object], state: Mapping[str, object] | None = None,
               budget: int = 900) -> dict[str, object]:
    if not name.isascii() or not name.replace("-", "").isalnum() or len(name) > 48:
        raise TypedPolicyRefusal("typed-policy-name-refused")
    request = {"schema": REQUEST_SCHEMA, "observation": dict(observation)}
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
                        capture=work / f"typed-{name}.process.json")
    if result.returncode != 0:
        raise TypedPolicyRefusal("typed-policy-refused")
    try:
        raw = output_path.read_bytes()
        value = json.loads(raw)
    except (OSError, json.JSONDecodeError) as error:
        raise TypedPolicyRefusal("typed-policy-output-refused") from error
    if len(raw) > MAX_POLICY_BYTES or not isinstance(value, dict) or value.get("schema") != STATE_SCHEMA:
        raise TypedPolicyRefusal("typed-policy-output-refused")
    return value


def validate_join(*, runner, source_root: pathlib.Path, work: pathlib.Path,
                  admission: Mapping[str, object], placement_sha: str, run_id: str, run_attempt: str,
                  source_sha: str, source_tree: str, inventory_sha256: str) -> str:
    request = {"schema":"fsgg.fourd.typed-source-join-request/1", "admission":dict(admission),
               "placementSha":placement_sha, "runId":run_id, "runAttempt":run_attempt,
               "observedSourceSha":source_sha, "observedSourceTree":source_tree,
               "observedInventorySha256":inventory_sha256}
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
          source_sha: str, source_tree: str, inventory_sha256: str) -> dict[str, object]:
    value = validate_join(runner=runner, source_root=source_root, work=work, admission=admission,
                          placement_sha=placement_sha, run_id=run_id, run_attempt=run_attempt,
                          source_sha=source_sha, source_tree=source_tree, inventory_sha256=inventory_sha256)
    state = transition(runner=runner, source_root=source_root, work=work, name="acquire",
                       observation={"kind": "acquire", "identity": value,
                                    "resource": "source-plaintext", "cost": 1})
    state = transition(runner=runner, source_root=source_root, work=work, name="validate",
                       observation={"kind": "validate", "identity": value, "cost": 1}, state=state)
    state = transition(runner=runner, source_root=source_root, work=work, name="admit",
                       observation={"kind": "admit", "identity": value, "cost": 1}, state=state)
    if state.get("effectEligible") is not True:
        raise TypedPolicyRefusal("typed-policy-admission-refused")
    return state
