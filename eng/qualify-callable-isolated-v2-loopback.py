#!/usr/bin/env python3
"""Controlled HTTP proof for a staged exact isolated-operation v2 artifact.

This qualification binds repository bytes, then executes the staged operator
against a local HTTP server. It has no GitHub credential or live provider URL.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import http.server
import importlib.util
import json
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import threading


ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = "eng/callable-cli-isolated-operation-v2.py"
CONTROLS = "eng/tests/fsc07-isolated-operation/test_versioned_operator_readback.py"
CONTRACT = "eng/callable-cli-isolated-operation-v2-contract.json"
PROPOSAL = "eng/callable-cli-isolated-operation-v2-proposal.json"
PREFLIGHT = "evidence/github-substrate-v2/gs2-09-9/isolated-operation-preflight.json"
SHA_A = "a" * 40
SHA_B = "b" * 40
SHA_C = "c" * 40
SENTINEL = "FSC07_LOOPBACK_SECRET_SENTINEL"


class Refused(Exception):
    """A bounded, public qualification refusal."""


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def require(value: bool, reason: str) -> None:
    if not value:
        raise Refused(reason)


def stage_exact_artifact(destination: pathlib.Path) -> tuple[pathlib.Path, dict]:
    contract = json.loads((ROOT / CONTRACT).read_bytes())
    expected = {
        SOURCE: contract["source"]["operationSourceSha256"],
        CONTROLS: contract["qualificationControls"]["sha256"],
        PREFLIGHT: contract["historicalPreflight"]["sha256"],
    }
    for relative in (SOURCE, CONTROLS, CONTRACT, PROPOSAL, PREFLIGHT):
        origin = ROOT / relative
        require(origin.is_file() and not origin.is_symlink(), "stage-input-unavailable")
        raw = origin.read_bytes()
        if relative in expected:
            require(sha(raw) == expected[relative], "stage-input-digest-drift")
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as stream:
            stream.write(raw)
            stream.flush()
        target.chmod(0o600)
        require(target.read_bytes() == raw, "stage-copy-drift")
    return destination / SOURCE, contract


def load_staged_operator(path: pathlib.Path):
    name = "fsgg_installed_isolated_v2_loopback"
    specification = importlib.util.spec_from_file_location(name, path)
    require(specification is not None and specification.loader is not None,
            "stage-import-unavailable")
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    specification.loader.exec_module(module)
    return module


def check_installed_inspect(source: pathlib.Path) -> None:
    bundle = source.parents[1]
    command = [sys.executable, str(source), "--contract", str(bundle / CONTRACT),
               "--proposal", str(bundle / PROPOSAL), "--preflight",
               str(bundle / PREFLIGHT), "inspect"]
    result = subprocess.run(command, capture_output=True, text=True, check=False,
                            timeout=20)
    require(result.returncode == 0, "staged-inspect-refused")
    observed = json.loads(result.stdout)
    require(observed.get("authorized") is False
            and observed.get("historicalObservationOnly") is True
            and observed.get("liveEffects") == 0,
            "staged-inspect-authority-drift")


class State:
    def __init__(self, operator, effect: str, outcome: str,
                 response_mode: str = "lost"):
        self.operator = operator
        self.effect = effect
        self.outcome = outcome
        self.response_mode = response_mode
        self.writes = 0
        self.foreign_requests = 0
        self.applied = False
        self.lock = threading.Lock()
        self.expected_pull = operator.ExpectedPull(
            operator.OPERATION_IDENTITY, 1, 44, "FS-GG/disposable",
            "refs/heads/source", SHA_A, "refs/heads/main", SHA_B)
        self.expected_protection = operator.ExpectedProtection(
            operator.OPERATION_IDENTITY, 1, 44, "FS-GG/disposable",
            "main", SHA_B, "required-check", 17)

    def pull(self) -> dict:
        expected = self.expected_pull
        head_sha = SHA_C if self.outcome == "wrong-head" else SHA_A
        return {
            "number": 8, "node_id": "PR_8", "state": "open", "draft": False,
            "merged": False, "title": self.operator.PULL_TITLE,
            "body": self.operator.pull_request_body(expected)["body"],
            "head": {"ref": "source", "sha": head_sha,
                     "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
            "base": {"ref": "main", "sha": SHA_B,
                     "repo": {"id": 44, "full_name": "FS-GG/disposable"}},
        }

    def policy(self) -> dict:
        policy = self.operator.protection_body(self.expected_protection)
        if self.outcome == "force-push":
            policy["allow_force_pushes"] = {"enabled": True}
        return policy


@contextlib.contextmanager
def loopback(state: State):
    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, _format, *_args):
            return

        def send_json(self, status: int, value: object) -> None:
            raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)

        def do_GET(self):
            prefix = "/repos/FS-GG/disposable"
            with state.lock:
                if self.path == prefix:
                    status, value = 200, {"id": 44, "full_name": "FS-GG/disposable"}
                elif self.path == prefix + "/git/ref/heads/source":
                    status, value = 200, {"ref": "refs/heads/source", "object": {"sha": SHA_A}}
                elif self.path == prefix + "/git/ref/heads/main":
                    status, value = 200, {"ref": "refs/heads/main", "object": {"sha": SHA_B}}
                elif self.path == prefix + "/pulls?state=open&per_page=100&page=1":
                    status, value = 200, [state.pull()] if state.applied else []
                elif self.path == prefix + "/pulls?state=open&per_page=100&page=2":
                    status, value = 200, []
                elif self.path == prefix + "/pulls/8" and state.applied:
                    status, value = 200, state.pull()
                elif self.path == prefix + "/branches/main":
                    status, value = 200, {"name": "main", "commit": {"sha": SHA_B},
                                           "protected": state.applied}
                elif self.path == prefix + "/branches/main/protection":
                    status, value = (200, state.policy()) if state.applied else (404, {})
                else:
                    state.foreign_requests += 1
                    status, value = 404, {"message": "unmatched-controlled-path"}
            self.send_json(status, value)

        def write_once(self, expected_path: str, expected_body: dict) -> None:
            length = int(self.headers.get("Content-Length", "0"))
            raw = self.rfile.read(length)
            try:
                body = json.loads(raw)
            except ValueError:
                body = None
            if self.path != expected_path or body != expected_body:
                self.send_json(400, {"message": "controlled-request-mismatch"})
                return
            with state.lock:
                state.writes += 1
                state.applied = True
            if state.response_mode == "error-body":
                self.send_json(500, {"message": SENTINEL})
                return
            if state.response_mode == "unauthorized":
                self.send_json(401, {"message": SENTINEL})
                return
            if state.response_mode == "redirect":
                self.send_response(302)
                self.send_header("Location",
                                 f"http://127.0.0.1:{self.server.server_address[1]}/foreign")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            # The write happened, but the client receives no response.
            self.close_connection = True
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.connection.close()

        def do_POST(self):
            self.write_once("/repos/FS-GG/disposable/pulls",
                            state.operator.pull_request_body(state.expected_pull))

        def do_PUT(self):
            self.write_once("/repos/FS-GG/disposable/branches/main/protection",
                            state.operator.protection_body(state.expected_protection))

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.daemon_threads = True
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)


def invoke(operator, state: State, base_url: str, journal: pathlib.Path):
    transport = operator.LoopbackHttpTransport(base_url)
    reserve = operator.SqliteAttemptFence(str(journal)).reserve_once
    if state.effect == "create-pull":
        return operator.run_pull_once(state.expected_pull, transport, reserve)
    return operator.run_protection_once(state.expected_protection, transport, reserve)


def replay_fresh_process(source: pathlib.Path, base_url: str,
                         journal: pathlib.Path, effect: str) -> dict:
    code = """
import importlib.util,json,sys
path,base,journal,effect=sys.argv[1:]
spec=importlib.util.spec_from_file_location('installed_v2_replay',path)
module=importlib.util.module_from_spec(spec)
sys.modules[spec.name]=module
spec.loader.exec_module(module)
transport=module.LoopbackHttpTransport(base)
reserve=module.SqliteAttemptFence(journal).reserve_once
if effect=='create-pull':
    expected=module.ExpectedPull(module.OPERATION_IDENTITY,1,44,'FS-GG/disposable','refs/heads/source','a'*40,'refs/heads/main','b'*40)
    result=module.run_pull_once(expected,transport,reserve)
else:
    expected=module.ExpectedProtection(module.OPERATION_IDENTITY,1,44,'FS-GG/disposable','main','b'*40,'required-check',17)
    result=module.run_protection_once(expected,transport,reserve)
print(json.dumps({'classification':type(result).__name__,'reason':result.reason if type(result) is module.Unknown else 'exact-poststate'}))
"""
    completed = subprocess.run(
        [sys.executable, "-c", code, str(source), base_url, str(journal), effect],
        capture_output=True, text=True, check=False, timeout=20)
    require(completed.returncode == 0, "fresh-process-replay-failed")
    return json.loads(completed.stdout)


def qualify(source: pathlib.Path, operator, temp: pathlib.Path, phase: str) -> list[str]:
    results: list[str] = []
    cases = (("create-pull", "exact", "ExactPull", "lost", None),
             ("create-pull", "wrong-head", "Unknown", "lost",
              "pull-request-readback-mismatch"),
             ("set-protection", "exact", "ExactProtection", "lost", None),
             ("set-protection", "force-push", "Unknown", "lost",
              "branch-protection-readback-mismatch"),
             ("create-pull", "exact", "ExactPull", "error-body", None),
             ("create-pull", "exact", "Unknown", "redirect",
              "pull-request-provider-explicit-refusal"),
             ("set-protection", "exact", "Unknown", "unauthorized",
              "branch-protection-provider-explicit-refusal"))
    for index, (effect, outcome, classification, response_mode, reason) in enumerate(cases):
        state = State(operator, effect, outcome, response_mode)
        journal = temp / f"attempts-{phase}-{index}.sqlite"
        with loopback(state) as base_url:
            result = invoke(operator, state, base_url, journal)
            require(type(result).__name__ == classification,
                    "loopback-poststate-classification")
            if reason is not None:
                require(result.reason == reason, "loopback-refusal-reason")
            require(state.writes == 1, "loopback-write-attempt-count")
            require(state.foreign_requests == 0, "loopback-redirect-followed")
            require(SENTINEL not in repr(result), "loopback-sensitive-output")
            results.append(f"{effect}:{outcome}:{response_mode}:{classification}")
            if phase == "recovery":
                # Reset the provider to absent while retaining the same durable
                # attempt journal; a new process must not issue another write.
                with state.lock:
                    state.applied = False
                replay = replay_fresh_process(source, base_url, journal, effect)
                require(replay.get("classification") == "Unknown"
                        and replay.get("reason") in {
                            "pull-request-attempt-not-reserved",
                            "branch-protection-attempt-not-reserved"},
                        "fresh-process-attempt-fence")
                require(state.writes == 1, "fresh-process-repeat-write")
                results.append(f"{effect}:{outcome}:{response_mode}:fresh-process-no-repeat")
    return results


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--phase", choices=("contract", "recovery"), required=True)
    args = parser.parse_args()
    try:
        with tempfile.TemporaryDirectory(prefix="fsgg-isolated-v2-loopback-") as name:
            temp = pathlib.Path(name)
            source, contract = stage_exact_artifact(temp / "bundle")
            check_installed_inspect(source)
            operator = load_staged_operator(source)
            cases = qualify(source, operator, temp, args.phase)
            print(json.dumps({
                "schema": "fsgg.coordination.callable-isolated-v2-loopback-qualification/1",
                "phase": args.phase,
                "artifactKind": "staged-source-copy",
                "stagedExactArtifact": sha(source.read_bytes()) == contract["source"]["operationSourceSha256"],
                "loopbackHttp": True,
                "authorized": False,
                "liveEffects": 0,
                "cases": cases,
            }, sort_keys=True, separators=(",", ":")))
        return 0
    except (OSError, ValueError, Refused, subprocess.TimeoutExpired) as error:
        reason = str(error) if type(error) is Refused else type(error).__name__
        print(f"loopback-qualification-refused:{reason}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
