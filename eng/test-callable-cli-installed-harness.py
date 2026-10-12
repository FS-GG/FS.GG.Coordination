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
import sys
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


def check_execute_main_refusal(tool: pathlib.Path, output: pathlib.Path) -> int:
    # Required receipt is checked before observers, credentials, or native effects.
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(("FSGG_V2_", "V2_ORDINARY_", "GITHUB_")) and key != "GH_TOKEN"}
    actual = subprocess.run([str(tool.resolve(strict=True)), "ordinary-settlement", "execute-main"],
                            env=env, capture_output=True, timeout=15, check=False)
    expected = b'ordinary-settlement-refused:SettlementProviderUnavailable "missing-environment:FSGG_V2_PREFLIGHT_RECEIPT"\n'
    if actual.returncode != 3 or actual.stdout != b"" or actual.stderr != expected:
        raise RuntimeError("installed execute-main did not preserve the recognized credential-free refusal")
    output.write_bytes(compact({"schema":"fsgg.coordination.installed-route-refusal/1",
                               "command":"ordinary-settlement execute-main", "exit":3,
                               "diagnostic":expected.decode().strip(), "nativePositive":False}) + b"\n")
    return 0



def write_pair_installed_project(root: pathlib.Path) -> tuple[pathlib.Path, list[str]]:
    import xml.etree.ElementTree as ET
    project = root / "PairInstalled.fsproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><Compile Include="Program.fs"/><PackageReference Include="FSharp.Core" Version="[10.1.302]"/><PackageReference Include="FS.GG.Execution.Contracts" Version="[0.2.0]"/><PackageReference Include="FS.GG.Execution.Processes" Version="[0.2.0]"/></ItemGroup></Project>')
    isolation = {"ImportDirectoryBuildProps": "false", "ImportDirectoryBuildTargets": "false",
                 "ImportDirectoryPackagesProps": "false", "ManagePackageVersionsCentrally": "false",
                 "DisableImplicitLibraryPacksFolder": "true", "DisableImplicitFSharpCoreReference": "true",
                 "RestoreFallbackFolders": "", "RestoreAdditionalProjectFallbackFolders": "",
                 "EnableTargetingPackDownload": "false", "EnableRuntimePackDownload": "false", "UsePackageDownload": "false"}
    # Global properties take effect before parent-import discovery or SDK implicit packs.
    # The actual evaluated SDK project must meet them before collecting restore inputs.
    doc = ET.parse(project); top = doc.getroot()
    target = ET.SubElement(top, "Target", Name="VerifyPairIsolation", BeforeTargets="CollectPackageReferences;CollectPackageDownloads;Build", DependsOnTargets="ProcessFrameworkReferences")
    for name, value in isolation.items():
        ET.SubElement(target, "Error", Condition="'$(" + name + ")' != '" + value + "'", Text="pair isolation property changed: " + name)
    ET.SubElement(target, "Error", Condition="'@(PackageDownload)' != ''", Text="pair unexpected PackageDownload")
    ET.SubElement(target, "Error", Condition="'@(PackageReference->Count())' != '3'", Text="pair unexpected PackageReference population")
    ET.SubElement(target, "WriteLinesToFile", File=str(root / "effective-isolation.txt"), Lines="PAIR_EFFECTIVE_ISOLATION_OK", Overwrite="true")
    references = ET.SubElement(top, "Target", Name="VerifyPairCompilerInputs", AfterTargets="ResolveReferences", BeforeTargets="CoreCompile")
    items = ET.SubElement(references, "ItemGroup")
    ET.SubElement(items, "PairCompilerInput", Include="@(ReferencePath)", Condition="'%(ReferencePath.Filename)' == 'FS.GG.Execution.Contracts' Or '%(ReferencePath.Filename)' == 'FS.GG.Execution.Processes'")
    ET.SubElement(references, "Error", Condition="'@(ProjectReference)' != ''", Text="pair producer project reference forbidden")
    ET.SubElement(references, "Error", Condition="'@(PairCompilerInput->Count())' != '2'", Text="pair compiler reference population changed")
    ET.SubElement(references, "WriteLinesToFile", File=str(root / "compiler-pair-references.txt"), Lines="@(PairCompilerInput->'%(FullPath)')", Overwrite="true")
    doc.write(project, encoding="utf-8", xml_declaration=True)
    properties = ["-p:" + name + "=" + value for name, value in isolation.items()]
    return project, properties

def check_execution_pair(candidate: pathlib.Path, mode: str, output: pathlib.Path) -> int:
    """Compile/load libraries from isolated package inputs; no process fixture/adoption claim."""
    if mode not in {"candidate", "public"}:
        raise RuntimeError("Execution pair input mode changed")
    ids = ("FS.GG.Execution.Contracts", "FS.GG.Execution.Processes")
    manifest = json.loads((candidate / "execution-pair-release-manifest.json").read_bytes())
    if manifest["profile"] != "execution-pair" or manifest["version"] != "0.2.0":
        raise RuntimeError("Execution pair manifest identity changed")
    rows = manifest["packages"]
    if len(rows) != 2 or {row["packageId"] for row in rows} != set(ids):
        raise RuntimeError("Execution pair member population changed")
    expected_dlls = {}
    for row in rows:
        archive = candidate / row["file"]
        if digest(archive.read_bytes()) != row["sha256"]:
            raise RuntimeError("Execution pair archive changed")
        with zipfile.ZipFile(archive) as package:
            expected_dlls[row["packageId"]] = package.read("lib/net10.0/" + row["packageId"] + ".dll")
    with tempfile.TemporaryDirectory(prefix="fsgg-execution-installed-") as temporary:
        root = pathlib.Path(temporary)
        environment = {key: value for key, value in os.environ.items() if key in {"PATH", "HOME", "LANG", "LC_ALL", "DOTNET_ROOT"}}
        environment.update({"DOTNET_CLI_HOME": str(root / "dotnet-home"), "NUGET_PACKAGES": str(root / "packages"),
                            "NUGET_HTTP_CACHE_PATH": str(root / "http-cache"), "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_PROCESSOR_COUNT": "4"})
        (root / "tmp").mkdir()
        environment.update(TMPDIR=str(root / "tmp"), TEMP=str(root / "tmp"), TMP=str(root / "tmp"))
        shutil.copyfile(ROOT / "global.json", root / "global.json")
        # Explicit source-only/private feeds and pre-existing caches are inaccessible to restore.
        import xml.etree.ElementTree as ET
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        if mode == "candidate": ET.SubElement(sources, "add", key="candidate", value=str(candidate.resolve(strict=True)))
        ET.SubElement(sources, "add", key="public", value="https://api.nuget.org/v3/index.json")
        ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
        mapping = ET.SubElement(config, "packageSourceMapping")
        if mode == "candidate":
            candidate_mapping = ET.SubElement(mapping, "packageSource", key="candidate")
            for id in ids: ET.SubElement(candidate_mapping, "package", pattern=id)
        public_mapping = ET.SubElement(mapping, "packageSource", key="public")
        for id in (("FSharp.Core",) if mode == "candidate" else ids + ("FSharp.Core",)):
            ET.SubElement(public_mapping, "package", pattern=id)
        config_path = root / "NuGet.Config"
        ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
        project, properties = write_pair_installed_project(root)
        (root / "Program.fs").write_text("""open System
open FS.GG.Execution.Contracts
open FS.GG.Execution.Processes
let factory: IProcessSessionFactory = ProcessSessions.createFactory()
if isNull (box factory) then failwith "missing installed factory"
let errors = Validation.identity { InvocationId = Guid.Empty; Execution = None; LaunchOrdinal = 0UL; SourceDigest = ""; ProfileId = ""; ProfileDigest = "" }
if List.isEmpty errors then failwith "invalid identity unexpectedly accepted"
printfn "EXECUTION_PAIR_INSTALLED_LOAD_OK"
""")
        run(["dotnet", "restore", str(project), "--configfile", str(config_path), "--no-cache", "--disable-parallel", "-p:NuGetAudit=false", *properties], cwd=root, env=environment)
        if (root / "effective-isolation.txt").read_bytes().strip() != b"PAIR_EFFECTIVE_ISOLATION_OK":
            raise RuntimeError("effective restore isolation guard did not execute")
        run(["dotnet", "build", str(project), "--no-restore", "--configuration", "Release", "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", *properties], cwd=root, env=environment)
        compiled = root / "bin/Release/net10.0"
        joins = []
        assets = json.loads((root / "obj/project.assets.json").read_bytes())
        expected_folder = str((root / "packages").resolve()) + os.sep
        if set(assets["packageFolders"]) != {expected_folder} or assets["project"]["restore"]["packagesPath"] != str(root / "packages"):
            raise RuntimeError("effective NuGet package/fallback folder escaped isolation")
        if assets["project"]["restore"].get("fallbackFolders", []) or set(assets["project"]["restore"]["sources"]) != ({str(candidate.resolve()), "https://api.nuget.org/v3/index.json"} if mode == "candidate" else {"https://api.nuget.org/v3/index.json"}):
            raise RuntimeError("effective NuGet source/fallback configuration changed")
        if set(assets["libraries"]) != {"FSharp.Core/10.1.302", *(id + "/0.2.0" for id in ids)} or any(row["type"] != "package" for row in assets["libraries"].values()):
            raise RuntimeError("effective installed dependency population changed")
        compiler_paths = (root / "compiler-pair-references.txt").read_text().splitlines()
        expected_paths = {(root / "packages" / id.lower() / "0.2.0" / "lib/net10.0" / (id + ".dll")).resolve(strict=True) for id in ids}
        if len(compiler_paths) != 2 or {pathlib.Path(value).resolve(strict=True) for value in compiler_paths} != expected_paths:
            raise RuntimeError("actual pair compiler references escaped isolated installed packages")
        for id in ids:
            compiler = next(path for path in expected_paths if path.name == id + ".dll")
            if compiler.read_bytes() != expected_dlls[id]: raise RuntimeError("actual pair compiler bytes differ from candidate payload")
            if id + "/0.2.0" not in assets["libraries"] or assets["libraries"][id + "/0.2.0"]["type"] != "package":
                raise RuntimeError("Execution pair did not resolve as exact installed package")
            actual = (compiled / (id + ".dll")).read_bytes()
            if actual != expected_dlls[id]: raise RuntimeError("Installed pair runtime differs from original candidate payload")
            joins.append({"packageId": id, "version": "0.2.0", "runtimeSha256": digest(actual)})
        actual = run(["dotnet", str(compiled / "PairInstalled.dll")], cwd=root, env=environment)
        if actual.stdout != "EXECUTION_PAIR_INSTALLED_LOAD_OK\n" or actual.stderr:
            raise RuntimeError("Installed pair did not produce exact bounded load marker")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(compact({"schema": "fsgg.coordination.execution-pair-installed-load/1", "mode": mode,
                                   "producerProjectReferenced": False, "packageInputsOnly": True, "physicalSourceExclusionEstablished": False,
                                   "privateCachesUnavailable": True, "effectiveProjectIsolationGuard": True, "effectiveNuGetFoldersVerified": True, "actualCompilerReferencesVerified": True, "publicOnly": mode == "public",
                                   "packages": joins, "nativeFixtureInvoked": False, "consumerAdoptionAccepted": False}) + b"\n")
    return 0

def main() -> int:
    if len(sys.argv) == 5 and sys.argv[1] == "--execution-pair":
        return check_execution_pair(pathlib.Path(sys.argv[2]), sys.argv[3], pathlib.Path(sys.argv[4]))
    if len(sys.argv) == 4 and sys.argv[1] == "--execute-main-refusal":
        return check_execute_main_refusal(pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3]))
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
