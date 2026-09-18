#!/usr/bin/env python3
"""Exercise the published callable tool against a durable loopback GitHub surface."""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import json
import os
import pathlib
import shutil
import socket
import subprocess
import tempfile
import threading
import urllib.request
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
CONTRACT = ROOT / "eng/callable-cli-installed-harness.json"
PACKAGE_URL = "https://api.nuget.org/v3-flatcontainer/fs.gg.coordination.cli/0.1.0/fs.gg.coordination.cli.0.1.0.nupkg"
SHA = {letter: letter * 40 for letter in "abcdef9"}


def compact(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


class State:
    def __init__(self) -> None:
        self.epoch = "OpenV2"
        self.epoch_generation = 3
        self.repository_id = 101
        self.node_id = "PR_kwDOordinary"
        self.base_sha = SHA["a"]
        self.head_sha = SHA["b"]
        self.policy_sha = SHA["c"]
        self.epoch_sha = SHA["9"]
        self.journal_ref = SHA["d"]
        self.journal: bytes | None = None
        self.blob_sha = SHA["e"]
        self.merged = False
        self.merge_commit = SHA["f"]
        self.check_conclusion = "success"
        self.incomplete_checks = False
        self.outage = False
        self.unknown_dispatch = False
        self.lose_journal_ack_at: int | None = None
        self.journal_writes = 0
        self.dispatches = 0
        self.requests: list[str] = []

    def request_hash(self, method: str, path: str, body: bytes) -> None:
        self.requests.append(digest(compact({"bodySha256": digest(body), "method": method, "path": path})))


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, state: State):
        super().__init__(("127.0.0.1", 0), Handler)
        self.state = state


class Handler(http.server.BaseHTTPRequestHandler):
    server: Server

    def log_message(self, *_: object) -> None:
        return

    def reply(self, status: int, value: object, headers: dict[str, str] | None = None) -> None:
        body = compact(value)
        self.send_response(status)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(body)))
        for name, item in (headers or {}).items():
            self.send_header(name, item)
        self.end_headers()
        self.wfile.write(body)

    def drop(self) -> None:
        try:
            self.connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.connection.close()

    def do_GET(self) -> None:  # noqa: N802
        state = self.server.state
        state.request_hash("GET", self.path, b"")
        if state.outage:
            self.drop()
            return
        path = self.path.split("?", 1)[0]
        if path == "/repos/FS-GG/FS.GG.Coordination":
            self.reply(200, {"full_name": "FS-GG/FS.GG.Coordination", "id": state.repository_id, "permissions": {"push": True}})
        elif path == "/repos/FS-GG/FS.GG.Coordination/pulls/7":
            self.reply(200, {"base": {"ref": "main", "sha": state.base_sha}, "head": {"sha": state.head_sha}, "merge_commit_sha": state.merge_commit, "merged": state.merged, "node_id": state.node_id})
        elif path.endswith("/git/ref/heads/policy"):
            self.reply(200, {"object": {"sha": state.policy_sha}})
        elif path.endswith("/git/ref/heads/epoch"):
            self.reply(200, {"object": {"sha": state.epoch_sha}})
        elif "/git/ref/heads/fsgg/v2/journal/operation/" in path:
            self.reply(200, {"object": {"sha": state.journal_ref}})
        elif path.endswith("/contents/epoch.json"):
            epoch = compact({"complete": True, "generation": state.epoch_generation, "phase": state.epoch})
            self.reply(200, {"content": base64.b64encode(epoch).decode()})
        elif path.endswith("/protection/required_status_checks"):
            self.reply(200, {"checks": [{"app_id": 10, "context": "required"}]})
        elif path.endswith("/check-runs"):
            headers = {}
            if state.incomplete_checks:
                headers["link"] = f'<http://127.0.0.1:{self.server.server_port}{self.path}>; rel="next"'
            self.reply(200, {"check_runs": [{"app": {"id": 10}, "conclusion": state.check_conclusion, "id": 1, "name": "required", "status": "completed"}], "total_count": 1}, headers)
        elif "/contents/ordinary/" in path:
            if state.journal is None:
                self.reply(404, {})
            else:
                self.reply(200, {"content": base64.b64encode(state.journal).decode(), "sha": state.blob_sha})
        else:
            self.reply(404, {"error": "unmatched-read"})

    def do_PUT(self) -> None:  # noqa: N802
        state = self.server.state
        length = int(self.headers.get("content-length", "0"))
        body = self.rfile.read(length)
        state.request_hash("PUT", self.path, body)
        path = self.path.split("?", 1)[0]
        if "/contents/ordinary/" in path:
            value = json.loads(body)
            expected = value.get("sha")
            if (state.journal is None and expected is not None) or (state.journal is not None and expected != state.blob_sha):
                self.reply(409, {})
                return
            state.journal = base64.b64decode(value["content"])
            state.journal_writes += 1
            state.blob_sha = str(state.journal_writes % 10) * 40
            if state.lose_journal_ack_at == state.journal_writes:
                self.drop()
            else:
                self.reply(201, {})
        elif path.endswith("/pulls/7/merge"):
            state.dispatches += 1
            state.merged = True
            if state.unknown_dispatch:
                self.drop()
            else:
                self.reply(200, {"merged": True, "sha": state.merge_commit})
        else:
            self.reply(404, {"error": "unmatched-write"})


def run(command: list[str], *, cwd: pathlib.Path, env: dict[str, str], expected: set[int] = {0}) -> subprocess.CompletedProcess[str]:
    completed = subprocess.run(command, cwd=cwd, env=env, text=True, capture_output=True, timeout=120, check=False)
    if completed.returncode not in expected:
        raise RuntimeError(f"command failed ({completed.returncode}): {' '.join(command[:2])}\n{completed.stdout}\n{completed.stderr}")
    return completed


def install_tool(root: pathlib.Path, contract: dict[str, object]) -> tuple[pathlib.Path, dict[str, str]]:
    package = root / "FS.GG.Coordination.Cli.0.1.0.nupkg"
    with urllib.request.urlopen(PACKAGE_URL, timeout=30) as response:
        package.write_bytes(response.read())
    expected = contract["artifact"]["nugetOrgServedSha256"]
    if digest(package.read_bytes()) != expected:
        raise RuntimeError("nuget.org served package digest changed")
    config = root / "NuGet.Config"
    config.write_text('<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources></configuration>', encoding="utf-8")
    tool = root / "tool"
    execution = root / "execution"
    execution.mkdir()
    env = os.environ.copy()
    env.update({"DOTNET_CLI_HOME": str(root / "dotnet-home"), "DOTNET_NOLOGO": "1", "NUGET_PACKAGES": str(root / "nuget"), "HARNESS_GITHUB_TOKEN": "loopback-only"})
    run(["dotnet", "tool", "install", "FS.GG.Coordination.Cli", "--version", "0.1.0", "--tool-path", str(tool), "--configfile", str(config)], cwd=execution, env=env)
    command = tool / "fsgg-coordination"
    if not command.is_file():
        raise RuntimeError("installed command is missing")
    return command, env


def with_server(state: State):
    server = Server(state)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, thread


def invoke(command: pathlib.Path, env: dict[str, str], execution: pathlib.Path, server: Server, verb: str, plan: pathlib.Path | None = None) -> subprocess.CompletedProcess[str]:
    args = [str(command), "delivery", verb, "--provider", "github", "--repository", "FS-GG/FS.GG.Coordination", "--pr", "7", "--token-env", "HARNESS_GITHUB_TOKEN", "--policy-ref", "heads/policy", "--epoch-repository", "FS-GG/.github", "--epoch-ref", "heads/epoch", "--epoch-path", "epoch.json", "--journal-repository", "FS-GG/FS.GG.Coordination", "--api-base", f"http://127.0.0.1:{server.server_port}/"]
    if plan is not None:
        args.extend(["--plan", str(plan)])
    return run(args, cwd=execution, env=env, expected={0, 2, 3})


def is_acceptance(completed: subprocess.CompletedProcess[str], *, replay: bool = False) -> bool:
    if completed.returncode != 0 or "AdvancePending" in completed.stdout:
        return False
    return ("AdvanceAlreadySettled" if replay else "AdvanceSettled") in completed.stdout


def execute_scenario(command: pathlib.Path, env: dict[str, str], execution: pathlib.Path, configure, expected_pending: str | None = None, preconfigure=None) -> tuple[State, list[dict[str, object]]]:
    state = State()
    if preconfigure is not None:
        preconfigure(state)
    server, thread = with_server(state)
    outputs: list[dict[str, object]] = []
    try:
        plan_result = invoke(command, env, execution, server, "plan")
        if plan_result.returncode != 0:
            raise RuntimeError(f"plan refused: {plan_result.stderr}")
        plan_path = execution / "plan.json"
        plan_path.write_bytes(plan_result.stdout.encode())
        configure(state, json.loads(plan_result.stdout))
        first = invoke(command, env, execution, server, "advance", plan_path)
        outputs.append({"accepted": is_acceptance(first), "code": first.returncode, "stdout": first.stdout.strip(), "stderr": first.stderr.strip()})
        if expected_pending is not None:
            if first.returncode != 0 or expected_pending not in first.stdout or outputs[-1]["accepted"]:
                raise RuntimeError(f"pending outcome was misclassified: {outputs[-1]}")
            state.unknown_dispatch = False
            state.lose_journal_ack_at = None
            recovered = invoke(command, env, execution, server, "advance", plan_path)
            outputs.append({"accepted": is_acceptance(recovered) or is_acceptance(recovered, replay=True), "code": recovered.returncode, "stdout": recovered.stdout.strip(), "stderr": recovered.stderr.strip()})
            replay = invoke(command, env, execution, server, "advance", plan_path)
            outputs.append({"accepted": is_acceptance(replay, replay=True), "code": replay.returncode, "stdout": replay.stdout.strip(), "stderr": replay.stderr.strip()})
            if not outputs[-2]["accepted"] or not outputs[-1]["accepted"] or state.dispatches != 1:
                raise RuntimeError("fresh-process recovery did not settle exactly once")
        return state, outputs
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--contract", default=str(CONTRACT))
    args = parser.parse_args()
    contract_bytes = pathlib.Path(args.contract).read_bytes()
    contract = json.loads(contract_bytes)
    if contract["schema"] != "fsgg.coordination.callable-cli-installed-harness-contract/1":
        raise RuntimeError("installed harness contract schema changed")

    request_hashes: list[str] = []
    journal_hashes: list[str] = []
    summaries: dict[str, object] = {}
    with tempfile.TemporaryDirectory(prefix="fsgg-callable-installed-") as scratch_value:
        scratch = pathlib.Path(scratch_value)
        command, env = install_tool(scratch, contract)
        execution = scratch / "execution"
        managed = next((scratch / "tool" / ".store" / "fs.gg.coordination.cli" / "0.1.0").rglob("FS.GG.Coordination.Cli.dll"))

        scenarios = [
            ("lost-intent-ack", lambda state, _: setattr(state, "lose_journal_ack_at", 1), "intent-outcome-unknown"),
            ("lost-pending-ack", lambda state, _: setattr(state, "lose_journal_ack_at", 2), "pending-journal-outcome-unknown"),
            ("lost-settlement-ack", lambda state, _: setattr(state, "lose_journal_ack_at", 3), "settlement-outcome-unknown"),
            ("lost-dispatch-response", lambda state, _: setattr(state, "unknown_dispatch", True), "dispatch-outcome-unknown"),
        ]
        for name, configure, pending in scenarios:
            state, outputs = execute_scenario(command, env, execution, configure, pending)
            request_hashes.extend(state.requests)
            if state.journal is not None:
                journal_hashes.append(digest(state.journal))
            summaries[name] = {"dispatches": state.dispatches, "firstAccepted": outputs[0]["accepted"], "journalWrites": state.journal_writes, "recovered": outputs[1]["accepted"], "replayNoOp": outputs[2]["accepted"]}

        def native_completion(state: State, plan: dict[str, object]) -> None:
            state.journal = compact({"generation": 2, "mergeCommit": None, "operationId": plan["operationId"], "planDigest": plan["seal"], "schema": "fsgg.coordination.ordinary-delivery-journal/1", "stage": "effect-pending"})
            state.merged = True

        state, outputs = execute_scenario(command, env, execution, native_completion)
        if not outputs[0]["accepted"] or state.dispatches != 0:
            raise RuntimeError("native completion was not reconciled without dispatch")
        request_hashes.extend(state.requests)
        journal_hashes.append(digest(state.journal or b""))
        summaries["native-completion"] = {"dispatches": 0, "readback": "AdvanceSettled"}

        negative_cases = [
            ("pre-open", lambda state: None, lambda state: setattr(state, "epoch", "OperatingV1"), "PreOpenV2Refusal"),
            ("stale-policy", lambda state: setattr(state, "policy_sha", "4" * 40), None, "StalePolicy"),
            ("stale-source", lambda state: setattr(state, "head_sha", "5" * 40), None, "ChangedSource"),
            ("stale-base", lambda state: setattr(state, "base_sha", "6" * 40), None, "ChangedSource"),
            ("stale-check", lambda state: setattr(state, "check_conclusion", "failure"), None, "ChangedSource"),
            ("stale-epoch", lambda state: setattr(state, "epoch_generation", 4), None, "StaleEpoch"),
            ("wrong-subject", lambda state: setattr(state, "node_id", "PR_wrong"), None, "CrossSubjectObservation"),
            ("incomplete-pagination", lambda state: setattr(state, "incomplete_checks", True), None, "check-pagination-incomplete"),
        ]
        for name, mutate, preconfigure, expected in negative_cases:
            def configure(state: State, _: dict[str, object], action=mutate) -> None:
                action(state)
            state, outputs = execute_scenario(command, env, execution, configure, preconfigure=preconfigure)
            first = outputs[0]
            if first["code"] == 0 or expected not in str(first["stderr"]) or state.dispatches != 0 or state.journal_writes != 0:
                raise RuntimeError(f"negative control failed: {name}: {first}")
            request_hashes.extend(state.requests)
            summaries[name] = {"code": first["code"], "zeroEffect": True}

        outage_expected: list[str] = []

        def observer_outage(state: State, plan: dict[str, object]) -> None:
            state.journal = compact({"generation": 2, "mergeCommit": None, "operationId": plan["operationId"], "planDigest": plan["seal"], "schema": "fsgg.coordination.ordinary-delivery-journal/1", "stage": "effect-pending"})
            outage_expected.append(digest(state.journal))
            state.outage = True

        state, outputs = execute_scenario(command, env, execution, observer_outage)
        retained = digest(state.journal or b"")
        if outputs[0]["code"] == 0 or "network-failure" not in str(outputs[0]["stderr"]) or state.dispatches != 0 or retained != outage_expected[0]:
            raise RuntimeError("observer outage erased or advanced durable delivery state")
        request_hashes.extend(state.requests)
        journal_hashes.append(retained)
        summaries["observer-outage"] = {"code": outputs[0]["code"], "journalRetained": True, "zeroEffect": True}

        report = {
            "artifact": contract["artifact"],
            "externalEffects": {"nugetOrgReadOnly": True, "provider": "loopback-only", "providerMutations": 0},
            "installedCommand": {"path": "tools/net10.0/any/FS.GG.Coordination.Cli.dll", "sha256": digest(managed.read_bytes())},
            "journal": {"finalStateDigests": sorted(journal_hashes), "setSha256": digest(compact(sorted(journal_hashes)))},
            "operationProposal": contract["operationProposal"],
            "qualification": {"negativeControls": len(negative_cases) + 1, "recoveryScenarios": len(scenarios) + 1, "results": summaries},
            "receiver": contract["receiver"],
            "requests": {"count": len(request_hashes), "sequenceSha256": digest(compact(request_hashes))},
            "schema": "fsgg.coordination.callable-cli-installed-harness-result/1",
        }
        print(compact(report).decode())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
