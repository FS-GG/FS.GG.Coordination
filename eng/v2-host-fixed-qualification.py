#!/usr/bin/env python3
"""Qualify one reviewed Host/runner pair in a disposable fixed job."""

from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import tempfile
import zipfile


SCHEMA = "fsgg.coordination.v2-host-fixed-qualification-profile/1"
RESULT_SCHEMA = "fsgg.coordination.v2-host-fixed-qualification-result/1"
REPOSITORY = "FS-GG/FS.GG.Coordination"
HOST_SCHEMA = "fsgg.coordination.orchestration-host-candidate/1"
RUNNER_SCHEMA = "fsgg.coordination.orchestration-runner-client-candidate/1"
HOST_VERIFICATION = "fsgg.coordination.orchestration-host-verification/1"
RUNNER_VERIFICATION = "fsgg.coordination.orchestration-runner-client-verification/1"
HOST_ROOT = "fsgg-coord-orchestration-host-linux-x64"
RUNNER_ROOT = "fsgg-coord-orchestration-runner-linux-x64"
HOST_PAYLOAD = "fsgg-coord-orchestration-host"
RUNNER_PAYLOAD = "fsgg-coord-orchestration-runner"
HOST_PROFILE_SCHEMA = "fsgg.orchestration.host-fixed-qualification/1"
HOST_RESULT_SCHEMA = "fsgg.orchestration.host-fixed-qualification-result/1"
OPERATION = "executor-compatibility/1"
RUNNER_PROTOCOL = "fsgg.orchestration.compatibility-diagnostic-response/1"
ADAPTER_VERSION = "codex-subscription-exec/1"
CREDENTIAL_SCOPE = "codex-subscription-login-status-read-only"
CLEANUP_KIND = "delete-owned-workspace/1"
SHA256 = re.compile(r"^[0-9a-f]{64}$")
SHA = re.compile(r"^[0-9a-f]{40}$")
VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
FORBIDDEN_PROFILE_KEYS = {
    "args", "arguments", "command", "commands", "continuation", "environment",
    "followup", "follow-up", "model", "recipe", "script", "shell", "url",
}


def refuse(code: str, detail: str) -> "NoReturn":
    raise SystemExit(f"{code} {detail}")


def require(condition: bool, code: str, detail: str) -> None:
    if not condition:
        refuse(code, detail)


def exact_keys(value: object, expected: set[str], code: str) -> dict:
    require(isinstance(value, dict), code, "expected object")
    actual = set(value)
    require(actual == expected, code, f"keys differ: {sorted(actual ^ expected)!r}")
    return value


def canonical_bytes(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode() + b"\n"


def digest_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def digest_file(path: Path) -> str:
    require(path.is_file() and not path.is_symlink(), "V2HQ-FILE", f"missing or symlinked file: {path.name}")
    with path.open("rb") as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def parse_time(value: object, code: str) -> datetime:
    require(isinstance(value, str), code, "expected UTC timestamp")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
    except ValueError:
        refuse(code, "timestamp is invalid")
    require(parsed.utcoffset() == timedelta(0), code, "timestamp is not UTC")
    return parsed


def reject_forbidden_keys(value: object) -> None:
    if isinstance(value, dict):
        for key, nested in value.items():
            require(key.lower() not in FORBIDDEN_PROFILE_KEYS, "V2HQ-INJECTION", f"forbidden profile field: {key}")
            reject_forbidden_keys(nested)
    elif isinstance(value, list):
        for nested in value:
            reject_forbidden_keys(nested)


def validate_component(value: object, role: str, source: str) -> dict:
    component = exact_keys(value, {
        "artifactDigest", "artifactId", "artifactName", "archiveFile", "archiveSha256", "payloadSha256",
        "runId", "verificationArtifactId", "verificationArtifactName", "verificationSha256",
    }, "V2HQ-PROFILE")
    expected_name = ("orchestration-host-linux-x64-" if role == "host" else "orchestration-runner-client-linux-x64-") + source
    expected_verification = ("orchestration-host-verification-" if role == "host" else "orchestration-runner-client-verification-") + source
    expected_archive = ("fsgg-coord-orchestration-host-linux-x64-" if role == "host" else "fsgg-coord-orchestration-runner-linux-x64-") + source + ".zip"
    for key in ("artifactId", "runId", "verificationArtifactId"):
        require(type(component[key]) is int and component[key] > 0, "V2HQ-PROFILE", f"{role}.{key} is invalid")
    require(component["artifactName"] == expected_name, "V2HQ-PROFILE", f"{role} artifact name differs")
    require(component["verificationArtifactName"] == expected_verification, "V2HQ-PROFILE", f"{role} verification name differs")
    require(component["archiveFile"] == expected_archive, "V2HQ-PROFILE", f"{role} archive file differs")
    for key in ("artifactDigest", "archiveSha256", "payloadSha256", "verificationSha256"):
        require(isinstance(component[key], str) and SHA256.fullmatch(component[key]) is not None,
                "V2HQ-PROFILE", f"{role}.{key} is invalid")
    return component


def load_profile(path: Path, now: datetime | None = None) -> dict:
    require(path.is_file() and not path.is_symlink(), "V2HQ-PROFILE", "profile must be a regular file")
    raw = path.read_bytes()
    require(len(raw) <= 16 * 1024, "V2HQ-PROFILE", "profile exceeds 16 KiB")
    try:
        value = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError):
        refuse("V2HQ-PROFILE", "profile is not JSON")
    require(canonical_bytes(value) == raw, "V2HQ-PROFILE", "profile is not canonical JSON")
    reject_forbidden_keys(value)
    profile = exact_keys(value, {"expiresAt", "host", "operation", "repository", "schema", "sourceRevision", "validFrom"}, "V2HQ-PROFILE")
    require(profile["schema"] == SCHEMA and profile["repository"] == REPOSITORY, "V2HQ-PROFILE", "authority differs")
    source = profile["sourceRevision"]
    require(isinstance(source, str) and SHA.fullmatch(source) is not None, "V2HQ-PROFILE", "source revision is invalid")
    validate_component(profile["host"], "host", source)
    validate_component(profile["operation"].get("runner") if isinstance(profile["operation"], dict) else None, "runner", source)
    operation = exact_keys(profile["operation"], {"cleanupRequired", "expectedCodexVersion", "kind", "runner", "timeoutSeconds"}, "V2HQ-PROFILE")
    require(operation["kind"] == "executor-compatibility-diagnostic", "V2HQ-INJECTION", "operation kind differs")
    require(operation["cleanupRequired"] is True, "V2HQ-PROFILE", "cleanup is not mandatory")
    require(type(operation["timeoutSeconds"]) is int and 1 <= operation["timeoutSeconds"] <= 30,
            "V2HQ-PROFILE", "timeout is outside 1..30 seconds")
    require(isinstance(operation["expectedCodexVersion"], str) and VERSION.fullmatch(operation["expectedCodexVersion"]) is not None,
            "V2HQ-INJECTION", "Codex version is not canonical")
    valid_from = parse_time(profile["validFrom"], "V2HQ-PROFILE")
    expires_at = parse_time(profile["expiresAt"], "V2HQ-PROFILE")
    require(valid_from < expires_at, "V2HQ-PROFILE", "profile interval is empty")
    observed = now or datetime.now(timezone.utc)
    require(valid_from <= observed < expires_at, "V2HQ-EXPIRED", "profile is not currently valid")
    return profile


def artifact_outputs(profile: dict) -> list[tuple[str, str]]:
    runner = profile["operation"]["runner"]
    return [
        ("host-run-id", str(profile["host"]["runId"])),
        ("host-candidate-id", str(profile["host"]["artifactId"])),
        ("host-verification-id", str(profile["host"]["verificationArtifactId"])),
        ("runner-run-id", str(runner["runId"])),
        ("runner-candidate-id", str(runner["artifactId"])),
        ("runner-verification-id", str(runner["verificationArtifactId"])),
    ]


def validate_verification(path: Path, component: dict, source: str, role: str) -> dict:
    require(digest_file(path) == component["verificationSha256"], "V2HQ-ARTIFACT", f"{role} verification digest differs")
    try:
        value = json.loads(path.read_bytes())
    except json.JSONDecodeError:
        refuse("V2HQ-ARTIFACT", f"{role} verification is not JSON")
    require(canonical_bytes(value) == path.read_bytes(), "V2HQ-ARTIFACT", f"{role} verification is not canonical")
    expected_schema = HOST_VERIFICATION if role == "host" else RUNNER_VERIFICATION
    require(value.get("schema") == expected_schema and value.get("candidate") == source,
            "V2HQ-ARTIFACT", f"{role} verification source differs")
    artifact = value.get("artifact", {})
    require(artifact.get("id") == component["artifactId"] and artifact.get("name") == component["artifactName"],
            "V2HQ-ARTIFACT", f"{role} verification identity differs")
    expected_url = (f"https://github.com/{REPOSITORY}/actions/runs/{component['runId']}"
                    f"/artifacts/{component['artifactId']}")
    require(artifact.get("url") == expected_url and artifact.get("digest") == component["artifactDigest"],
            "V2HQ-ARTIFACT", f"{role} verification route or outer digest differs")
    require(value.get("archive", {}).get("sha256") == component["archiveSha256"],
            "V2HQ-ARTIFACT", f"{role} verification archive differs")
    require(value.get("payload", {}).get("sha256") == component["payloadSha256"],
            "V2HQ-ARTIFACT", f"{role} verification payload differs")
    return value


def extract_payload(archive: Path, component: dict, source: str, role: str, output: Path) -> Path:
    require(archive.name == component["archiveFile"], "V2HQ-ARTIFACT", f"{role} archive name differs")
    require(digest_file(archive) == component["archiveSha256"], "V2HQ-ARTIFACT", f"{role} archive digest differs")
    root = HOST_ROOT if role == "host" else RUNNER_ROOT
    payload_name = HOST_PAYLOAD if role == "host" else RUNNER_PAYLOAD
    expected = [f"{root}/{payload_name}", f"{root}/manifest.json"]
    with zipfile.ZipFile(archive) as zipped:
        infos = zipped.infolist()
        require([info.filename for info in infos] == expected, "V2HQ-ARTIFACT", f"{role} archive layout differs")
        require(all(not Path(info.filename).is_absolute() and ".." not in Path(info.filename).parts for info in infos),
                "V2HQ-ARTIFACT", f"{role} archive contains unsafe path")
        payload = zipped.read(expected[0])
        manifest_raw = zipped.read(expected[1])
    require(digest_bytes(payload) == component["payloadSha256"], "V2HQ-ARTIFACT", f"{role} payload digest differs")
    manifest = json.loads(manifest_raw)
    require(canonical_bytes(manifest) == manifest_raw, "V2HQ-ARTIFACT", f"{role} manifest is not canonical")
    expected_schema = HOST_SCHEMA if role == "host" else RUNNER_SCHEMA
    require(manifest.get("schema") == expected_schema and manifest.get("sourceRevision") == source,
            "V2HQ-ARTIFACT", f"{role} manifest source differs")
    require(manifest.get("payload", {}).get("sha256") == component["payloadSha256"],
            "V2HQ-ARTIFACT", f"{role} manifest payload differs")
    output.mkdir(mode=0o700)
    executable = output / payload_name
    executable.write_bytes(payload)
    executable.chmod(stat.S_IRUSR | stat.S_IXUSR)
    return executable


def process_count(root: Path) -> int:
    marker = str(root)
    count = 0
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if marker.encode() in (entry / "cmdline").read_bytes():
                count += 1
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            pass
    return count


def run_fixed_process(arguments: list[str], environment: dict[str, str], timeout: int) -> tuple[int, str, str]:
    process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, env=environment, start_new_session=True)
    previous: dict[int, object] = {}

    def cancel(signum: int, _frame: object) -> None:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.communicate()
        refuse("V2HQ-CANCELLED", f"received signal {signum}; process group removed")

    try:
        for signum in (signal.SIGINT, signal.SIGTERM):
            previous[signum] = signal.getsignal(signum)
            signal.signal(signum, cancel)
        try:
            stdout, stderr = process.communicate(timeout=timeout + 2)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.communicate()
            refuse("V2HQ-TIMEOUT", "Host exceeded the fixed deadline")
        return process.returncode, stdout, stderr
    finally:
        for signum, handler in previous.items():
            signal.signal(signum, handler)


def materialize_host_profile(profile: dict, path: Path, host: Path, runner: Path,
                             provider: Path, workspace: Path) -> dict:
    source = profile["sourceRevision"]
    value = {
        "schema": HOST_PROFILE_SCHEMA,
        "operation": OPERATION,
        "revision": f"v2-host-fixed-qualification/1@{source}",
        "hostExecutableSha256": profile["host"]["payloadSha256"],
        "runnerExecutable": str(runner),
        "runnerExecutableSha256": profile["operation"]["runner"]["payloadSha256"],
        "providerExecutable": str(provider),
        "providerExecutableSha256": digest_file(provider),
        "expectedRunnerProtocol": RUNNER_PROTOCOL,
        "expectedAdapterVersion": ADAPTER_VERSION,
        "expectedCodexVersion": profile["operation"]["expectedCodexVersion"],
        "environmentAllowList": ["PATH"],
        "credentialScope": CREDENTIAL_SCOPE,
        "maximumRuntimeSeconds": profile["operation"]["timeoutSeconds"],
        "expiresAt": profile["expiresAt"],
        "disposableWorkspace": str(workspace),
        "cleanup": CLEANUP_KIND,
    }
    require(not workspace.exists(), "V2HQ-WORKSPACE", "disposable workspace already exists")
    path.write_bytes(canonical_bytes(value))
    return value


def validate_host_result(path: Path, host_profile_path: Path, expected: dict) -> dict:
    require(path.is_file() and not path.is_symlink(), "V2HQ-HOST", "Host result is absent")
    try:
        value = json.loads(path.read_bytes())
    except json.JSONDecodeError:
        refuse("V2HQ-HOST", "Host result is not JSON")
    result = exact_keys(value, {
        "schema", "operation", "profileRevision", "profileSha256", "hostExecutableSha256",
        "runnerExecutableSha256", "providerExecutableSha256", "runnerProtocol", "adapterVersion",
        "credentialScope", "environmentAllowList", "maximumRuntimeSeconds", "startedAt", "completedAt",
        "disposition", "detail", "diagnostic", "cleanup",
    }, "V2HQ-HOST")
    require(result["schema"] == HOST_RESULT_SCHEMA and result["operation"] == OPERATION,
            "V2HQ-HOST", "Host result contract differs")
    require(result["profileRevision"] == expected["revision"]
            and result["profileSha256"] == digest_file(host_profile_path),
            "V2HQ-HOST", "Host result profile binding differs")
    for result_key, profile_key in (
        ("hostExecutableSha256", "hostExecutableSha256"),
        ("runnerExecutableSha256", "runnerExecutableSha256"),
        ("providerExecutableSha256", "providerExecutableSha256"),
        ("runnerProtocol", "expectedRunnerProtocol"),
        ("adapterVersion", "expectedAdapterVersion"),
        ("credentialScope", "credentialScope"),
        ("environmentAllowList", "environmentAllowList"),
        ("maximumRuntimeSeconds", "maximumRuntimeSeconds"),
    ):
        require(result[result_key] == expected[profile_key], "V2HQ-HOST", f"Host result {result_key} differs")
    require(result["disposition"] == "passed" and result["detail"] == "passed",
            "V2HQ-HOST", f"Host refused: {result['detail']!r}")
    diagnostic = result["diagnostic"]
    require(isinstance(diagnostic, dict)
            and diagnostic.get("scope") == "compatibility-diagnostic-only"
            and diagnostic.get("schema") == "fsgg.orchestration.served-compatibility-diagnostic/1"
            and diagnostic.get("adapterVersion") == ADAPTER_VERSION
            and diagnostic.get("versionState") == "matched",
            "V2HQ-HOST", "Host diagnostic differs")
    cleanup = exact_keys(result["cleanup"], {
        "processTreeTerminationRequired", "processTreeTerminated",
        "workspaceRemovalAttempted", "workspaceRemoved",
    }, "V2HQ-HOST")
    require(cleanup == {
        "processTreeTerminationRequired": True, "processTreeTerminated": True,
        "workspaceRemovalAttempted": True, "workspaceRemoved": True,
    }, "V2HQ-CLEANUP", "Host cleanup is incomplete")
    require(parse_time(result["startedAt"], "V2HQ-HOST") <= parse_time(result["completedAt"], "V2HQ-HOST"),
            "V2HQ-HOST", "Host result time interval differs")
    return result


def qualify(profile_path: Path, artifacts: Path, output: Path, workflow_revision: str) -> None:
    profile = load_profile(profile_path)
    require(SHA.fullmatch(workflow_revision) is not None, "V2HQ-WORKFLOW", "workflow revision is invalid")
    require(not output.exists(), "V2HQ-OUTPUT", "output must not already exist")
    source = profile["sourceRevision"]
    host_component = profile["host"]
    runner_component = profile["operation"]["runner"]
    with tempfile.TemporaryDirectory(prefix="v2-host-fixed-qualification-") as temporary:
        root = Path(temporary)
        host_download = artifacts / "host-candidate"
        runner_download = artifacts / "runner-candidate"
        validate_verification(artifacts / "host-verification" / "verification.json", host_component, source, "host")
        validate_verification(artifacts / "runner-verification" / "verification.json", runner_component, source, "runner")
        host = extract_payload(host_download / host_component["archiveFile"], host_component, source, "host", root / "host")
        runner = extract_payload(runner_download / runner_component["archiveFile"], runner_component, source, "runner", root / "runner")
        provider_log = root / "provider.log"
        provider = root / "controlled-provider"
        provider.write_text("#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + str(provider_log) + "'\n"
                            "test \"$1\" = --version && { echo 'codex-cli '" + profile["operation"]["expectedCodexVersion"] + "; exit 0; }\n"
                            "test \"$1 $2\" = 'login status' && { echo 'Logged in using ChatGPT'; exit 0; }\n"
                            "echo model-session-refused >&2\nexit 91\n")
        provider.chmod(stat.S_IRUSR | stat.S_IXUSR)
        working = root / "owned-workspace"
        host_profile_path = root / "reviewed-host-profile.json"
        host_profile = materialize_host_profile(profile, host_profile_path, host, runner, provider, working)
        result_path = root / "host-result.json"
        untouched = {
            "profile": digest_file(profile_path),
            "host": digest_file(host),
            "runner": digest_file(runner),
            "provider": digest_file(provider),
            "hostArchive": digest_file(host_download / host_component["archiveFile"]),
            "runnerArchive": digest_file(runner_download / runner_component["archiveFile"]),
        }
        arguments = [str(host), "qualify-fixed-job", "--profile", str(host_profile_path), "--result", str(result_path)]
        environment = {"PATH": os.environ.get("PATH", "/usr/bin:/bin")}
        returncode, stdout, stderr = run_fixed_process(arguments, environment, profile["operation"]["timeoutSeconds"])
        require(returncode == 0, "V2HQ-HOST", f"exit={returncode} stderr={stderr.strip()!r}")
        require(stdout == "", "V2HQ-HOST", "Host polluted stdout")
        host_result = validate_host_result(result_path, host_profile_path, host_profile)
        calls = provider_log.read_text().splitlines() if provider_log.exists() else []
        require(calls == ["--version", "login status"], "V2HQ-MODEL", f"provider calls differ: {calls!r}")
        require(process_count(root) == 0, "V2HQ-CLEANUP", "a qualification process remains")
        require(not working.exists(), "V2HQ-CLEANUP", "disposable working directory remains")
        observed_untouched = {
            "profile": digest_file(profile_path),
            "host": digest_file(host),
            "runner": digest_file(runner),
            "provider": digest_file(provider),
            "hostArchive": digest_file(host_download / host_component["archiveFile"]),
            "runnerArchive": digest_file(runner_download / runner_component["archiveFile"]),
        }
        require(observed_untouched == untouched, "V2HQ-SURFACE", "a declared untouched surface changed")
        result = {
            "schema": RESULT_SCHEMA, "sourceRevision": source, "workflowRevision": workflow_revision,
            "profileSha256": digest_file(profile_path),
            "hostPayloadSha256": host_component["payloadSha256"],
            "runnerPayloadSha256": runner_component["payloadSha256"],
            "scope": "compatibility-diagnostic-only", "outcome": "passed", "cleanup": "complete",
            "providerObservations": ["version", "login-status"], "modelSessions": 0, "followUpWork": 0,
            "untouchedSurfaceCount": len(untouched),
            "hostResultSha256": digest_file(result_path),
        }
        output.mkdir(mode=0o700)
        (output / "result.json").write_bytes(canonical_bytes(result))
    print(f"V2_HOST_FIXED_QUALIFICATION_OK source={source} resultSha256={digest_file(output / 'result.json')}")


def main() -> None:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    validate = sub.add_parser("validate-profile")
    validate.add_argument("--profile", required=True)
    outputs = sub.add_parser("workflow-values")
    outputs.add_argument("--profile", required=True)
    outputs.add_argument("--github-output", required=True)
    run = sub.add_parser("qualify")
    run.add_argument("--profile", required=True)
    run.add_argument("--artifacts", required=True)
    run.add_argument("--output", required=True)
    run.add_argument("--workflow-revision", required=True)
    args = parser.parse_args()
    profile_path = Path(args.profile).resolve()
    if args.command == "validate-profile":
        load_profile(profile_path)
        print("V2_HOST_FIXED_PROFILE_OK")
    elif args.command == "workflow-values":
        profile = load_profile(profile_path)
        output_path = Path(args.github_output).resolve()
        require(output_path.is_file() and not output_path.is_symlink(), "V2HQ-OUTPUT", "GitHub output file is unavailable")
        with output_path.open("a") as stream:
            for name, value in artifact_outputs(profile):
                stream.write(f"{name}={value}\n")
    else:
        qualify(profile_path, Path(args.artifacts).resolve(), Path(args.output).resolve(), args.workflow_revision)


if __name__ == "__main__":
    main()
