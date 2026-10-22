#!/usr/bin/env python3
"""Exercise the published callable tool against a durable loopback GitHub surface."""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import json
import io
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import urllib.request
import urllib.error
import time
import re
import signal
import zipfile

if sys.flags.optimize != 0:
    raise RuntimeError('protected release validators require Python optimize=0')


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



def pair_version(version: str) -> str:
    """Finite historical/current package profiles; never selects preparation/publication.

    The original 0.3.0 and historical 0.2.0 stay literal. Newer coherent pairs are the
    forward route's: its single version rule lives in eng/execution_pair_forward.py.
    """
    if version in ("0.2.0", "0.3.0"):
        return version
    import importlib.util
    spec = importlib.util.spec_from_file_location("execution_pair_forward_version", ROOT / "eng/execution_pair_forward.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not module.forward_version_allowed(version):
        raise RuntimeError("Execution pair version is outside the finite reviewed profiles")
    return version

def extract_execution_pair_artifact(admission: pathlib.Path, root: pathlib.Path, out: pathlib.Path) -> int:
    """Verify/extract the original three-file pair artifact; grants no effect authority."""
    a=json.loads(admission.read_bytes())
    version=pair_version(a['version'])
    assert a['tag']=='execution/v'+version
    assert set(a['packages'])=={'FS.GG.Execution.Contracts','FS.GG.Execution.Processes'}
    for name in ('run.json','jobs.json','artifact.json'): assert 0<(root/name).stat().st_size<=4194304
    run=json.loads((root/'run.json').read_bytes())
    assert run['id']==a['runId'] and run['repository']['id']==1346720714 and run['head_repository']['id']==1346720714
    assert run['head_branch']=='main' and run['event']=='workflow_dispatch' and run['head_sha']==a['source'] and type(run['run_attempt']) is int and run['run_attempt']==1
    assert run['status']=='completed' and run['conclusion']=='success' and run['path']=='.github/workflows/callable-cli-release-prepare.yml'
    jobs=json.loads((root/'jobs.json').read_bytes())
    assert type(jobs['total_count']) is int and jobs['total_count']==len(jobs['jobs']) and len(jobs['jobs'])<100
    selected=[job for job in jobs['jobs'] if job['id']==a['jobId']]; assert len(selected)==1
    job=selected[0]
    assert job['run_id']==a['runId'] and type(job['run_attempt']) is int and job['run_attempt']==1 and job['head_sha']==a['source']
    assert job['name']=='Prepare the routine Execution pair without publication' and job['status']=='completed' and job['conclusion']=='success'
    artifact=json.loads((root/'artifact.json').read_bytes())
    assert artifact['id']==a['artifactId'] and artifact['expired'] is False and artifact['name']=='execution-pair-'+a['source'] and artifact['digest']=='sha256:'+a['archiveSha256']
    assert artifact['workflow_run']['id']==a['runId'] and artifact['workflow_run']['head_sha']==a['source']
    archive=root/'candidate.zip'; assert 0<archive.stat().st_size<=50331648 and hashlib.sha256(archive.read_bytes()).hexdigest()==a['archiveSha256']
    expected={id+'.'+version+'.nupkg':value for id,value in a['packages'].items()}; expected['execution-pair-release-manifest.json']=a['manifestSha256']
    with zipfile.ZipFile(archive) as z:
        assert len(z.infolist())==3 and set(z.namelist())==set(expected)
        for entry in z.infolist():
            cap=1048576 if entry.filename.endswith('.json') else 16777216
            assert 0<entry.file_size<=cap and (entry.external_attr>>16)&0o170000!=0o120000
            data=z.read(entry); assert hashlib.sha256(data).hexdigest()==expected[entry.filename]
            with (out/entry.filename).open('xb') as target: target.write(data)
    manifest=json.loads((out/'execution-pair-release-manifest.json').read_bytes())
    assert manifest['schema']=='fsgg.coordination.execution-pair-release-preparation/1' and manifest['profile']=='execution-pair'
    assert manifest['version']==a['version'] and manifest['tag']==a['tag'] and manifest['sourceCommit']==a['source'] and manifest['sourceTree']==a['tree']
    assert manifest['publicationAuthorized'] is False and manifest['tagAuthorized'] is False and manifest['ownership']['reservation']=='unreserved'
    assert len(manifest['packages'])==2 and {row['packageId']:row['sha256'] for row in manifest['packages']}==a['packages']
    for row in manifest['packages']: assert row['file']==row['packageId']+'.'+version+'.nupkg'
    return 0

def write_pair_installed_project(root: pathlib.Path, version: str) -> tuple[pathlib.Path, list[str]]:
    import xml.etree.ElementTree as ET
    version=pair_version(version)
    project = root / "PairInstalled.fsproj"
    project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><DisableImplicitFSharpCoreReference>true</DisableImplicitFSharpCoreReference><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup><Compile Include="Program.fs"/><PackageReference Include="FSharp.Core" Version="[10.1.302]"/><PackageReference Include="FS.GG.Execution.Contracts" Version="[{version}]"/><PackageReference Include="FS.GG.Execution.Processes" Version="[{version}]"/></ItemGroup></Project>')
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
    version=pair_version(manifest["version"])
    if manifest["profile"] != "execution-pair" or manifest["tag"] != "execution/v"+version:
        raise RuntimeError("Execution pair manifest identity changed")
    rows = manifest["packages"]
    if len(rows) != 2 or {row["packageId"] for row in rows} != set(ids):
        raise RuntimeError("Execution pair member population changed")
    expected_dlls = {}
    for row in rows:
        if row["file"] != row["packageId"]+"."+version+".nupkg":
            raise RuntimeError("Execution pair member filename differs from selected version")
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
        project, properties = write_pair_installed_project(root,version)
        typed_factory = ("let textFactory: ITextProcessSessionFactory = ProcessSessions.createTextFactory()\nif isNull (box textFactory) then failwith \"missing installed text factory\"\n" if version != "0.2.0" else "")
        (root / "Program.fs").write_text("""open System
open FS.GG.Execution.Contracts
open FS.GG.Execution.Processes
let factory: IProcessSessionFactory = ProcessSessions.createFactory()
if isNull (box factory) then failwith "missing installed factory"
""" + typed_factory + """let errors = Validation.identity { InvocationId = Guid.Empty; Execution = None; LaunchOrdinal = 0UL; SourceDigest = ""; ProfileId = ""; ProfileDigest = "" }
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
        expected_folder = str(root / "packages")
        expected_folders = {expected_folder, expected_folder + os.sep}
        if len(assets["packageFolders"]) != 1 or not set(assets["packageFolders"]).issubset(expected_folders) or assets["project"]["restore"]["packagesPath"] != str(root / "packages"):
            restore = assets["project"]["restore"]
            details = json.dumps({"expectedPackagesPath": str(root / "packages"), "expectedPackageFolders": sorted(expected_folders),
                                  "observedPackagesPath": restore["packagesPath"], "observedPackageFolders": list(assets["packageFolders"]),
                                  "observedFallbackFolders": restore.get("fallbackFolders", []), "observedSources": list(restore.get("sources", {}))},
                                 sort_keys=True, separators=(",", ":"))
            if len(details.encode("utf-8")) > 4096:
                details = "observed path fields exceed the 4096-byte diagnostic bound"
            raise RuntimeError("effective NuGet package/fallback folder escaped isolation: " + details)
        if assets["project"]["restore"].get("fallbackFolders", []) or set(assets["project"]["restore"]["sources"]) != ({str(candidate.resolve()), "https://api.nuget.org/v3/index.json"} if mode == "candidate" else {"https://api.nuget.org/v3/index.json"}):
            raise RuntimeError("effective NuGet source/fallback configuration changed")
        if set(assets["libraries"]) != {"FSharp.Core/10.1.302", *(id + "/" + version for id in ids)} or any(row["type"] != "package" for row in assets["libraries"].values()):
            raise RuntimeError("effective installed dependency population changed")
        compiler_paths = (root / "compiler-pair-references.txt").read_text().splitlines()
        expected_paths = {(root / "packages" / id.lower() / version / "lib/net10.0" / (id + ".dll")).resolve(strict=True) for id in ids}
        if len(compiler_paths) != 2 or {pathlib.Path(value).resolve(strict=True) for value in compiler_paths} != expected_paths:
            raise RuntimeError("actual pair compiler references escaped isolated installed packages")
        for id in ids:
            compiler = next(path for path in expected_paths if path.name == id + ".dll")
            if compiler.read_bytes() != expected_dlls[id]: raise RuntimeError("actual pair compiler bytes differ from candidate payload")
            if id + "/" + version not in assets["libraries"] or assets["libraries"][id + "/" + version]["type"] != "package":
                raise RuntimeError("Execution pair did not resolve as exact installed package")
            actual = (compiled / (id + ".dll")).read_bytes()
            if actual != expected_dlls[id]: raise RuntimeError("Installed pair runtime differs from original candidate payload")
            joins.append({"packageId": id, "version": version, "runtimeSha256": digest(actual)})
        actual = run(["dotnet", str(compiled / "PairInstalled.dll")], cwd=root, env=environment)
        if actual.stdout != "EXECUTION_PAIR_INSTALLED_LOAD_OK\n" or actual.stderr:
            raise RuntimeError("Installed pair did not produce exact bounded load marker")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(compact({"schema": "fsgg.coordination.execution-pair-installed-load/1", "mode": mode,
                                   "producerProjectReferenced": False, "packageInputsOnly": True, "physicalSourceExclusionEstablished": False,
                                   "privateCachesUnavailable": True, "effectiveProjectIsolationGuard": True, "effectiveNuGetFoldersVerified": True, "actualCompilerReferencesVerified": True, "publicOnly": mode == "public",
                                   "packages": joins, "nativeFixtureInvoked": False, "consumerAdoptionAccepted": False}) + b"\n")
    return 0

# Original immutable 0.2 A retained as historical evidence; never current admission.
PAIR_HISTORICAL_CANDIDATE = {
    "source": "c6764ac0cd62cb14372c89c14234292315000380",
    "runId": 38274623070, "runAttempt": 1, "jobId": 114896943746,
    "artifactId": 11759090717,
    "archiveSha256": "6aedfb7e4eda796fdea3fc0326003a6f90f859352d7394d311f4c5d867687819",
    "manifestSha256": "4a09ead11044d34cccb2c305c563d95bf0781c918433d19c0bc631ee8b479e18",
    "packages": {
        "FS.GG.Execution.Contracts": "62b1f85b8622d6719f086d73f4dbf29bd251dbb083a9b9588535d6b86531665d",
        "FS.GG.Execution.Processes": "20d09f3bc71c78c46809931a8d1f7b4009d381fb0c4f203db2d7c846266e4f41",
    },
}


# Genuine original S1 A; observation pins alone never authorize publication.
PAIR_OBSERVATION_CANDIDATE = {'source': 'b11fb121f046898e655b779dfc6809f35fc7aac6', 'runId': 38455588652, 'jobId': 115497350577, 'artifactId': 11842360725, 'archiveSha256': '75b1b0a8ac11522a63043932abf01c6ad7048988300ee42edddc6dedc3f58791', 'manifestSha256': 'c8772596977e2eb172672fa393d78daab5867aefcf24aa9960a1c5627d4f7ca8', 'packages': {'FS.GG.Execution.Contracts': 'bfd248eb935b4868bb1a7fb431bdb1ec26d3c1fedd2a294c34cf7b546bed151a', 'FS.GG.Execution.Processes': '978ac179d1983b6a3346655cd904391fd8dc2ff7f3b0558ed9fd40fa9aa56cbd'}, 'runAttempt': 1}
PAIR_ACCOUNT_OWNER_EVIDENCE = {'evidenceKind': 'account-owner-provided', 'evidenceSha256': '47a06f26b2bbd9924df2a9778cfdeb8bf29ee618c88908f652dbe2cbb5019fcb', 'nugetUser': 'Paradigma11', 'repositoryId': 1346720714, 'repositoryOwnerId': 293607011, 'publisherWorkflow': '.github/workflows/callable-cli-release-publish.yml', 'firstPublicationCovered': ['FS.GG.Execution.Contracts', 'FS.GG.Execution.Processes']}

def pair_protection(actual, admission, desired):
    """One complete policy predicate for observation and effect admission."""
    keys = ('target', 'enforcement', 'conditions', 'bypass_actors', 'rules')
    if not isinstance(actual, dict) or any(key not in actual for key in (*keys, 'id')):
        raise ValueError('incomplete ruleset evidence: required policy fields are missing')
    if not isinstance(actual['bypass_actors'], list):
        raise ValueError('incomplete ruleset evidence: bypass_actors must be an explicit complete list')
    if actual['bypass_actors']:
        raise ValueError('ruleset policy refused: bypass actors are present')
    policy = json.loads(json.dumps({key: actual[key] for key in keys}))
    if not isinstance(policy['rules'], list) or any(not isinstance(r, dict) for r in policy['rules']):
        raise ValueError('ruleset rule population malformed')
    updates = [r for r in policy['rules'] if r.get('type') == 'update']
    if len(updates) != 1: raise ValueError('ruleset update population changed')
    update = updates[0]
    if set(update) == {'type'}: update['parameters'] = {'update_allows_fetch_and_merge': False}
    if (set(update) != {'type', 'parameters'} or update['parameters'] != {'update_allows_fetch_and_merge': False}
            or update['parameters']['update_allows_fetch_and_merge'] is not False):
        raise ValueError('ruleset update rule changed')
    selection = admission['tagProtection']
    expected_hash = '5182bb35a6dd682cef84ac9ce4210cfd09f38b7410d8c9cf9bae42866bcb81da'
    if (selection != {'rulesetId': 21633398, 'policySha256': expected_hash}
            or type(actual['id']) is not int or actual['id'] != 21633398
            or policy != {key: desired[key] for key in keys}
            or hashlib.sha256(compact(policy)).hexdigest() != expected_hash):
        raise ValueError('exact tag protection policy changed')
    return {'rulesetId': 21633398, 'policySha256': expected_hash, 'explicitBypassActors': [],
            'publicationAuthorized': False, 'publicationEligible': False}


PAIR_ORIGINAL_STEPS = (
    ('Set up job', 'success'),
    ('Check out reviewed publisher B without persisted credentials', 'success'),
    ('Bind exclusive disabled-by-default pair admission', 'success'),
    ('Bind pair A ancestry and publication-only B changes', 'success'),
    ('Authenticate and extract exactly the original pair A archive', 'success'),
    ('Authenticate original four receiver proofs before any publication effect', 'success'),
    ('Set up the pinned .NET SDK', 'success'),
    ('Attest only the retained pair inputs under reviewed B', 'success'),
    ('Verify pair provenance and candidate installed load before effects', 'success'),
    ('Observe four feed cells and component protection before first push', 'failure'),
    ('Obtain selected Trusted Publishing credentials before pair effects', 'skipped'),
    ('Recheck exact B and pair bytes immediately before first push', 'skipped'),
    ('Push both retained members to GitHub Packages first', 'skipped'),
    ('Verify both GitHub raw archives before any public push', 'skipped'),
    ('Push the identical retained pair inputs to nuget.org', 'skipped'),
    ('Verify each public payload and repository signature', 'skipped'),
    ('Validate public installed load and write exact pair readback', 'skipped'),
    ('Finalize component tag only after complete pair readback', 'skipped'),
    ('Preserve the original pair observation on success or failure', 'success'),
    ('Post Set up the pinned .NET SDK', 'skipped'),
    ('Post Check out reviewed publisher B without persisted credentials', 'success'),
    ('Complete job', 'success'))


PAIR_POLICY_TOKEN_CLEANUP_CONTRACT = {'enabled': False,
 'sourceContract': 'bounded-existing-token-cleanup',
 'apiVersion': '2026-03-10',
 'owner': 'FS-GG',
 'repository': 'FS.GG.Coordination',
 'repositoryId': 1346720714,
 'permissions': {'administration': 'write'},
 'knownTokenSequence': ['GET installation/repositories?per_page=1 = 200; selected scope exactly '
                        'one repository',
                        'DELETE installation/token = 204',
                        'GET same installation/repositories?per_page=1 = 401'],
 'requestSeconds': 10,
 'cleanupSeconds': 30,
 'terminationReserveSeconds': 5,
 'responseBytes': 1048576,
 'cleanupRequestMaximum': 3,
 'automaticPostDeleteMaximum': 1,
 'mintLogicalAttemptMaximum': 4,
 'mintWireSendMaximum': None,
 'unknownGrantsPolicy': 'held-until-authentic-reconciliation',
 'nativeAdmissionEstablished': False,
 'publicationAuthorized': False}


PAIR_PUBLISHER_REF = 'refs/tags/execution/v0.3.0-publisher-original38495729817'


def pair_publisher_binding(environment, ref_object, resolved_commit, previous=None):
    """Closed recovery ref; failed/missing/moved observations grant no effects."""
    publisher = environment.get('GITHUB_SHA', '')
    if (environment.get('GITHUB_REF') != PAIR_PUBLISHER_REF
            or environment.get('GITHUB_REPOSITORY') != 'FS-GG/FS.GG.Coordination'
            or environment.get('GITHUB_RUN_ATTEMPT') != '1'
            or environment.get('GITHUB_WORKFLOW_REF') != 'FS-GG/FS.GG.Coordination/.github/workflows/callable-cli-release-publish.yml@' + PAIR_PUBLISHER_REF
            or environment.get('REQUESTED_PUBLISHER') != publisher
            or re.fullmatch('[0-9a-f]{40}', publisher) is None
            or not isinstance(ref_object, dict) or ref_object.get('ref') != PAIR_PUBLISHER_REF
            or ref_object.get('object', {}).get('type') not in ('commit', 'tag')
            or re.fullmatch('[0-9a-f]{40}', ref_object.get('object', {}).get('sha', '')) is None
            or resolved_commit != publisher
            or ref_object.get('object', {}).get('type') == 'commit' and ref_object['object']['sha'] != publisher):
        raise ValueError('exact immutable publisher identity unavailable or changed')
    value = {'ref': PAIR_PUBLISHER_REF, 'publisher': publisher,
             'refObject': ref_object['object']['sha'], 'refType': ref_object['object']['type'],
             'workflow': '.github/workflows/callable-cli-release-publish.yml'}
    if previous is not None and previous != value:
        raise ValueError('immutable publisher moved; retain partial original, observation only')
    return value


def pair_publisher_recheck(environment, ref_object, resolved_commit, binding_path, paths_path, diff_paths, source_root):
    """Require original retained proofs and current exact source before further effects."""
    def retained(path):
        path = pathlib.Path(path)
        if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= 1048576:
            raise ValueError('retained immutable publisher proof missing or unsafe; observation only')
        value = json.loads(path.read_bytes())
        if not isinstance(value, dict):
            raise ValueError('retained immutable publisher proof malformed; observation only')
        return value
    previous = retained(binding_path)
    binding = pair_publisher_binding(environment, ref_object, resolved_commit, previous)
    manifest = retained(paths_path)
    paths = manifest.get('paths')
    if (set(manifest) != {'publisher', 'paths', 'workflowSha256'}
            or manifest['publisher'] != binding['publisher'] or not isinstance(paths, dict) or not paths
            or not isinstance(diff_paths, list) or len(set(diff_paths)) != len(diff_paths)
            or sorted(diff_paths) != sorted(paths)
            or any(not isinstance(p, str) or pathlib.Path(p).is_absolute() or '..' in pathlib.Path(p).parts for p in paths)):
        raise ValueError('retained publisher exact diff path population changed; observation only')
    root = pathlib.Path(source_root)
    for path, digest in paths.items():
        source = root / path
        if (not isinstance(digest, str) or re.fullmatch('[0-9a-f]{64}', digest) is None
                or source.is_symlink() or not source.is_file()
                or hashlib.sha256(source.read_bytes()).hexdigest() != digest):
            raise ValueError('retained publisher source path digest changed; observation only')
    workflow = root / binding['workflow']
    if (workflow.is_symlink() or not workflow.is_file()
            or hashlib.sha256(workflow.read_bytes()).hexdigest() != manifest['workflowSha256']):
        raise ValueError('retained publisher workflow digest changed; observation only')
    return binding


PAIR_PROMOTION_OPERATION = 'promote-execution-publisher-b11fb121-original38495729817'
PAIR_PROMOTION_SELECTION = {'enabled':False, 'operation':PAIR_PROMOTION_OPERATION,
    'ref':PAIR_PUBLISHER_REF, 'kind':'lightweight', 'sourceTrustAccepted':False}
PAIR_PROMOTION_CONTEXTS = {'bootstrap-recovery','compiler-and-tests','dependency-and-security',
                         'deterministic-build','evidence-manifest','package-install-smoke','routine-eligibility'}


PAIR_PROMOTION_PR_SKIPS = {
    ('.github/workflows/bootstrap-qualification.yml','qualification-economics'): "    if: ${{ github.event_name == 'schedule' }}",
    ('.github/workflows/optimistic-parallel-validation.yml','offline-formal-shadow'): "    if: ${{ false && github.event_name == 'pull_request' }}",
}


def pair_promotion_job_conclusion(run, name, workflow_source):
    """Only two exact PR skips justified by the already pinned workflow bytes."""
    condition=PAIR_PROMOTION_PR_SKIPS.get((run['path'],name))
    if condition is None:return 'success'
    marker='\n  '+name+':\n'
    if run['event']!='pull_request' or workflow_source.count(marker)!=1:
        raise ValueError('promotion skip has no exact trusted workflow/event job')
    block=re.split(r'\n  (?=\S)',workflow_source.split(marker,1)[1],maxsplit=1)[0]
    if condition not in block.splitlines():
        raise ValueError('promotion skip condition differs from pinned workflow source')
    return 'skipped'


def pair_promotion_acceptance(native, trust, publisher, now):
    """Verify pinned native evidence; trust/capabilities must come from root authority.

    Inert fixtures exercise these predicates. No JSON assertion authenticates its own
    acquisition or nominates an authorized signer/reviewer. The root must separately
    accept that contract and pin the authenticated acquisition in its authority.
    """
    if (not isinstance(trust,dict) or set(trust)!={'evidenceSha256','signer','reviewerId','repositoryId','workflows','deadline','observedAt','pullNumber','branch'}
            or trust['repositoryId']!=1346720714 or not 0<=now-trust['observedAt']<=60
            or not now<trust['deadline']<=trust['observedAt']+90
            or hashlib.sha256(compact(native)).hexdigest()!=trust['evidenceSha256']):
        raise ValueError('promotion root-pinned authenticated acquisition/trust unavailable or stale')
    if publisher in ('b11fb121f046898e655b779dfc6809f35fc7aac6','0ccfb6d067d912bbd8a1e26c4b56449358443b2e') or not re.fullmatch('[0-9a-f]{40}',publisher):raise ValueError('promotion exact signed B missing')
    commit=native['commit'];verification=commit['commit']['verification']
    if (commit['sha']!=publisher or native['repository']['id']!=1346720714
            or verification['verified'] is not True or verification['reason']!='valid'
            or not verification.get('signature') or not verification.get('payload')
            or commit['committer']['id']!=trust['signer']['id']
            or hashlib.sha256(verification['signature'].encode()).hexdigest()!=trust['signer']['signatureSha256']
            or hashlib.sha256(verification['payload'].encode()).hexdigest()!=trust['signer']['payloadSha256']
            or commit['commit']['tree']['sha']!=trust['signer']['tree']
            or [p['sha'] for p in commit['parents']]!=trust['signer']['parents']):
        raise ValueError('promotion signed final commit/signer/payload/tree/parent differs')
    pr=native['pull'];reviews=native['reviews'];threads=native['threads']
    if (pr['head']['ref']!=trust['branch'] or pr['number']!=trust['pullNumber'] or pr['base']['ref']!='main' or pr['head']['sha']!=publisher or pr['head']['repo']['id']!=1346720714
            or pr['base']['repo']['id']!=1346720714 or pr['state']!='open' or pr['merged'] is not False
            or reviews['total_count']!=len(reviews['reviews']) or not reviews['reviews']
            or threads['pageInfo']['hasNextPage'] is not False or any(t['isResolved'] is not True for t in threads['nodes'])
            or any(v['pull_request_url']!='https://api.github.com/repos/FS-GG/FS.GG.Coordination/pulls/'+str(trust['pullNumber']) or v['commit_id']!=publisher or v['state'] not in ('APPROVED','COMMENTED') for v in reviews['reviews'])
            or not any(v['user']['id']==trust['reviewerId'] for v in reviews['reviews'])):
        raise ValueError('promotion exact-B authorized native review/rejection/threads unavailable')
    checks=native['checks'];runs=native['runs'];jobs=native['jobs']
    if (type(checks['total_count']) is not int or checks['total_count']!=len(checks['check_runs']) or not 7<=checks['total_count']<100
            or not PAIR_PROMOTION_CONTEXTS<={c['name'] for c in checks['check_runs']}
            or len({c['id'] for c in checks['check_runs']})!=len(checks['check_runs'])
            or {c['id'] for c in checks['check_runs']}!={j['id'] for j in jobs['jobs']}
            or any(sum(c['name']==name for c in checks['check_runs'])!=1 for name in PAIR_PROMOTION_CONTEXTS)
            or type(runs['total_count']) is not int or runs['total_count']!=len(runs['workflow_runs']) or not runs['workflow_runs']
            or type(jobs['total_count']) is not int or jobs['total_count']!=len(jobs['jobs']) or len({j['id'] for j in jobs['jobs']})!=len(jobs['jobs']) or not jobs['jobs']):
        raise ValueError('promotion complete required/coherent native populations absent')
    seen=set()
    for run in runs['workflow_runs']:
        selection=trust['workflows'].get(run['path'])
        if (not selection or run['id'] in seen or run['head_sha']!=publisher or run['run_attempt']!=1
                or run['repository']['id']!=1346720714 or run['head_branch']!=trust['branch']
                or run['event']!=('pull_request_target' if run['path']=='.github/workflows/routine-eligibility.yml' else 'pull_request')
                or not isinstance(run.get('pull_requests'),list) or len(run['pull_requests'])!=1
                or run['pull_requests'][0].get('number')!=trust['pullNumber'] or run['pull_requests'][0].get('head',{}).get('sha')!=publisher
                or run['workflow_id']!=selection['id'] or run['status']!='completed' or run['conclusion']!='success'
                or hashlib.sha256(native['workflowBytes'][run['path']].encode()).hexdigest()!=selection['sha256']):
            raise ValueError('promotion trusted workflow/run/head/coherent result differs')
        seen.add(run['id'])
    if {run['path'] for run in runs['workflow_runs']}!=set(trust['workflows']) or set(trust['workflows'])!={'.github/workflows/bootstrap-qualification.yml','.github/workflows/routine-eligibility.yml','.github/workflows/optimistic-parallel-validation.yml'}:
        raise ValueError('promotion full coherent workflow absent')
    for c in checks['check_runs']:
        matched=[run for run in runs['workflow_runs'] if run['check_suite_id']==c['check_suite']['id']]
        job=[j for j in jobs['jobs'] if j['id']==c['id']]
        expected_path='.github/workflows/routine-eligibility.yml' if c['name']=='routine-eligibility' else '.github/workflows/bootstrap-qualification.yml'
        expected=pair_promotion_job_conclusion(matched[0],c['name'],native['workflowBytes'][matched[0]['path']]) if len(matched)==1 else None
        if (c['head_sha']!=publisher or c['app']['id']!=15368 or c['status']!='completed' or c['conclusion']!=expected
                or len(matched)!=1
                or c['name'] in PAIR_PROMOTION_CONTEXTS and matched[0]['path']!=expected_path
                or len(job)!=1 or job[0]['name']!=c['name'] or job[0]['run_id']!=matched[0]['id']
                or c['details_url']!='https://github.com/FS-GG/FS.GG.Coordination/actions/runs/'+str(matched[0]['id'])+'/job/'+str(c['id'])
                or job[0]['check_run_url']!='https://api.github.com/repos/FS-GG/FS.GG.Coordination/check-runs/'+str(c['id'])):
            raise ValueError('promotion required check origin/head/result differs')
    for job in jobs['jobs']:
        producer=[run for run in runs['workflow_runs'] if run['id']==job['run_id']]
        expected=pair_promotion_job_conclusion(producer[0],job['name'],native['workflowBytes'][producer[0]['path']]) if len(producer)==1 else None
        if (job['run_id'] not in seen or job['head_sha']!=publisher or job['run_attempt']!=1
                or job['status']!='completed' or job['conclusion']!=expected):
            raise ValueError('promotion native job population differs')
    for run in runs['workflow_runs']:
        for path,name in PAIR_PROMOTION_PR_SKIPS:
            if path==run['path'] and sum(j['run_id']==run['id'] and j['name']==name for j in jobs['jobs'])!=1:
                raise ValueError('promotion expected skipped job population absent or duplicated')
    if {j['run_id'] for j in jobs['jobs']}!=seen or native['tagBefore']!={'status':404,'ref':PAIR_PUBLISHER_REF}:
        raise ValueError('promotion tag preexists/unknown or native jobs incomplete')
    pair_protection(native['policy'], {'tagProtection':{'rulesetId':21633398,'policySha256':'5182bb35a6dd682cef84ac9ce4210cfd09f38b7410d8c9cf9bae42866bcb81da'}},native['policy'])
    return {'publisher':publisher,'operation':PAIR_PROMOTION_OPERATION,'reviewCommit':publisher,
            'acquisitionSha256':trust['evidenceSha256'],'sourcePredicatesPassed':True,
            'promotionAccepted':False,'publicationAuthorized':False}


def pair_promotion_response(value, publisher):
    if value!={'ref':PAIR_PUBLISHER_REF,'node_id':value.get('node_id'),'url':'https://api.github.com/repos/FS-GG/FS.GG.Coordination/git/refs/'+PAIR_PUBLISHER_REF[5:],
               'object':{'type':'commit','sha':publisher,'url':'https://api.github.com/repos/FS-GG/FS.GG.Coordination/git/commits/'+publisher}} or not value.get('node_id'):
        raise ValueError('promotion response exact lightweight ref/B differs; consumed unknown')
    return {'ref':PAIR_PUBLISHER_REF,'publisher':publisher,'creationResponseObserved':True,
            'independentReadback':'pending','promotionAccepted':False}


def pair_recovery_selection(admission):
    selected = admission['recovery']
    expected = {'enabled', 'policyObservationEnabled', 'clientQualificationEnabled', 'policyTokenCleanup', 'promotion', 'publisherRef', 'operation', 'originalOperation', 'publisher', 'runId', 'runAttempt',
                'jobId', 'artifactId', 'archiveSha256'}
    if set(selected) != expected or any(type(selected[k]) is not bool for k in ('enabled','policyObservationEnabled','clientQualificationEnabled')):
        raise ValueError('pair recovery selection changed')
    if selected['promotion'] != PAIR_PROMOTION_SELECTION:
        raise ValueError('promotion remains disabled pending source trust and native capability acceptance')
    if compact(selected['policyTokenCleanup']) != compact(PAIR_POLICY_TOKEN_CLEANUP_CONTRACT):
        raise ValueError('pair policy token cleanup closed source contract changed')
    if (selected['publisherRef'] != PAIR_PUBLISHER_REF
            or admission['enabled'] is not False
            or admission['operation'] != 'publish-execution-pair-030-b11fb121-four38484632631'
            or selected['originalOperation'] != admission['operation']
            or selected['operation'] != 'recover-execution-pair-030-b11fb121-original38495729817'
            or selected['publisher'] != '0ccfb6d067d912bbd8a1e26c4b56449358443b2e'
            or any(type(selected[k]) is not int or selected[k] != v for k, v in
                   {'runId': 38495729817, 'runAttempt': 1, 'jobId': 115630940784, 'artifactId': 11863691435}.items())
            or selected['archiveSha256'] != 'e885b5a0a80807e461559b89547dda91c9c822a89216145a0092b53e3a3c879e'):
        raise ValueError('original pair identity or consumed operation changed')
    return selected


def pair_original_native(admission, run, jobs):
    """Consume native terminal evidence; an archive is not send authority."""
    selected = pair_recovery_selection(admission)
    if (run['id'] != selected['runId'] or run['workflow_id'] != 361038177
            or run['repository']['id'] != 1346720714 or run['head_repository']['id'] != 1346720714
            or run['path'] != '.github/workflows/callable-cli-release-publish.yml'
            or run['event'] != 'workflow_dispatch' or run['head_branch'] != 'main'
            or run['head_sha'] != selected['publisher'] or type(run['run_attempt']) is not int
            or run['run_attempt'] != 1 or run['status'] != 'completed' or run['conclusion'] != 'failure'):
        raise ValueError('original publisher native run changed')
    rows = jobs['jobs']
    if (type(jobs['total_count']) is not int or jobs['total_count'] != len(rows) or not 1 <= len(rows) < 100
            or len({row['id'] for row in rows}) != len(rows)):
        raise ValueError('original publisher job population incomplete')
    matches = [row for row in rows if row['id'] == selected['jobId']]
    if len(matches) != 1: raise ValueError('original publisher job missing')
    job = matches[0]
    if (job['run_id'] != run['id'] or job['head_sha'] != run['head_sha'] or type(job['run_attempt']) is not int
            or job['run_attempt'] != 1 or job['status'] != 'completed' or job['conclusion'] != 'failure'
            or job['name'] != 'Publish the admitted original routine Execution pair'):
        raise ValueError('original publisher job identity changed')
    steps = job['steps']
    if ([(s['name'], s['conclusion']) for s in steps] != list(PAIR_ORIGINAL_STEPS)
            or any(s['status'] != 'completed' for s in steps)
            or [s['number'] for s in steps] != sorted({s['number'] for s in steps})):
        raise ValueError('original publisher complete ordered step outcomes changed')
    return {'runId': selected['runId'], 'jobId': selected['jobId'], 'completeOrderedSteps': True}


def pair_original_failure(admission, run, jobs, artifact, archive):
    """Authenticate retained bytes after complete native first-failure reconciliation."""
    pair_original_native(admission, run, jobs)
    selected = pair_recovery_selection(admission)
    if (artifact['id'] != selected['artifactId'] or artifact['expired'] is not False
            or artifact['name'] != 'execution-pair-publication-38495729817-1'
            or artifact['digest'] != 'sha256:' + selected['archiveSha256']
            or artifact['workflow_run']['id'] != run['id'] or artifact['workflow_run']['head_sha'] != run['head_sha']
            or not 0 < len(archive) <= 192 * 1024**2 or hashlib.sha256(archive).hexdigest() != selected['archiveSha256']):
        raise ValueError('original publisher observation archive changed')
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        entries = bundle.infolist()
        if len(entries) > 128 or len({e.filename for e in entries}) != len(entries):
            raise ValueError('original publisher observation ZIP population changed')
        original = bundle.getinfo('candidate.zip')
        if original.file_size > 50331648 or hashlib.sha256(bundle.read(original)).hexdigest() != admission['archiveSha256']:
            raise ValueError('original publisher candidate archive changed')
    return {'originalOperation': selected['originalOperation'], 'publisherSource': selected['publisher'],
            'runId': selected['runId'], 'runAttempt': 1, 'jobId': selected['jobId'],
            'observationArtifactId': selected['artifactId'], 'observationArchiveSha256': selected['archiveSha256'],
            'firstCause': 'incomplete-complete-protection-observation', 'attestationRetained': True,
            'packagePushes': 'skipped', 'historicalFeedUnchanged': 'unknown'}


def pair_verified_attestation(admission, verified):
    """Only canonical verifier output, not a decoded unverified DSSE statement."""
    selected = pair_recovery_selection(admission)
    repo = 'https://github.com/FS-GG/FS.GG.Coordination'
    workflow = '.github/workflows/callable-cli-release-publish.yml'
    signer = repo + '/' + workflow + '@refs/heads/main'
    invocation = repo + '/actions/runs/38495729817/attempts/1'
    expected_subjects = {k + '.0.3.0.nupkg': {'sha256': v} for k, v in admission['packages'].items()}
    if not isinstance(verified, list) or len(verified) != 1: raise ValueError('verified original attestation population changed')
    result = verified[0]['verificationResult']
    if result['mediaType'] != 'application/vnd.dev.sigstore.verificationresult+json;version=0.1':
        raise ValueError('canonical verified attestation missing')
    cert = result['signature']['certificate']
    expected_cert = {'issuer': 'https://token.actions.githubusercontent.com', 'subjectAlternativeName': signer,
                     'buildSignerURI': signer, 'buildSignerDigest': selected['publisher'], 'runnerEnvironment': 'github-hosted',
                     'sourceRepositoryURI': repo, 'sourceRepositoryDigest': selected['publisher'],
                     'sourceRepositoryRef': 'refs/heads/main', 'sourceRepositoryIdentifier': '1346720714',
                     'sourceRepositoryOwnerIdentifier': '293607011', 'buildConfigURI': signer,
                     'buildConfigDigest': selected['publisher'], 'buildTrigger': 'workflow_dispatch', 'runInvocationURI': invocation}
    if any(cert.get(k) != v for k, v in expected_cert.items()): raise ValueError('verified original signer claims changed')
    statement = result['statement']; subjects = statement['subject']; predicate = statement['predicate']
    if (statement['_type'] != 'https://in-toto.io/Statement/v1' or statement['predicateType'] != 'https://slsa.dev/provenance/v1'
            or len(subjects) != 2 or {s['name']: s['digest'] for s in subjects} != expected_subjects
            or predicate['buildDefinition'] != {
                'buildType': 'https://actions.github.io/buildtypes/workflow/v1',
                'externalParameters': {'workflow': {'path': workflow, 'ref': 'refs/heads/main', 'repository': repo}},
                'internalParameters': {'github': {'event_name': 'workflow_dispatch', 'repository_id': '1346720714',
                                                  'repository_owner_id': '293607011', 'runner_environment': 'github-hosted'}},
                'resolvedDependencies': [{'uri': 'git+' + repo + '@refs/heads/main', 'digest': {'gitCommit': selected['publisher']}}]}
            or predicate['runDetails']['builder'] != {'id': signer}
            or predicate['runDetails']['metadata']['invocationId'] != invocation):
        raise ValueError('verified original subject or provenance claims changed')
    return {'subjects': expected_subjects, 'publisherSource': selected['publisher'], 'invocation': invocation,
            'signatureVerifiedByCanonicalCaller': True, 'attestationCreationRepeated': False}


class PairObservationBound(RuntimeError):
    pass


class PairMetadataReader:
    """GET-only transport; no redirects, retries, credentials issued or payload downloads."""
    def __init__(self):
        self.deadline = time.monotonic() + 150
        self.count = 0
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        self.opener = urllib.request.build_opener(NoRedirect())

    def __call__(self, url, authenticated=False):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0 or self.count >= 20:
            raise PairObservationBound("metadata request/deadline budget exhausted")
        if not url.startswith(("https://api.github.com/", "https://api.nuget.org/v3-flatcontainer/")):
            raise RuntimeError("unselected metadata origin")
        if authenticated and not url.startswith("https://api.github.com/"):
            raise RuntimeError("credential origin refused")
        # This transport owns only its own watchdog. Never replace a caller's active budget.
        if any(value > 0 for value in signal.getitimer(signal.ITIMER_REAL)):
            raise PairObservationBound("active caller watchdog prevents metadata acquisition")
        self.count += 1
        headers = {"Accept": "application/vnd.github+json", "User-Agent": "coordination-pair-preflight"}
        if authenticated:
            headers["Authorization"] = "Bearer " + os.environ["GH_TOKEN"]
            headers["X-GitHub-Api-Version"] = "2022-11-28"
        # Bound the whole response, including a slowly streaming body, not only socket waits.
        def request_timeout(signum, frame):
            raise TimeoutError("metadata request time limit")
        previous_handler = signal.signal(signal.SIGALRM, request_timeout)
        signal.setitimer(signal.ITIMER_REAL, min(15, remaining))
        try:
            try:
                response = self.opener.open(urllib.request.Request(url, headers=headers, method="GET"), timeout=min(15, remaining))
            except urllib.error.HTTPError as error:
                response = error
            with response:
                raw = response.read(1048577)
                if len(raw) > 1048576:
                    raise PairObservationBound("metadata body exceeds 1 MiB")
                if time.monotonic() >= self.deadline:
                    raise PairObservationBound("metadata deadline exhausted")
                status = response.code
                if status != 200:
                    return status, None, hashlib.sha256(raw).hexdigest()
                return status, json.loads(raw), hashlib.sha256(raw).hexdigest()
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous_handler)



class PairPolicyTokenCleanup:
    """Closed same-bearer sequence; no retries, redirects or credential fallback."""
    scope_url = "https://api.github.com/installation/repositories?per_page=1"
    revoke_url = "https://api.github.com/installation/token"

    def __init__(self, token, *, opener=None, clock=time.monotonic):
        self.token = token
        self.clock = clock
        self.deadline = clock() + 30
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        self.opener = opener or urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def sequence(self):
        # The public surface has no arbitrary method/URL or authentication selector.
        for label, method, url in (("scope", "GET", self.scope_url),
                                   ("delete", "DELETE", self.revoke_url),
                                   ("rejection", "GET", self.scope_url)):
            try:
                yield label, self._request(method, url), None
            except Exception:
                # Exception text and response bodies may contain credentials; retain no raw text.
                yield label, (None, None), "request-incomplete"

    def _request(self, method, url):
        remaining = self.deadline - self.clock()
        if remaining <= 0:
            raise PairObservationBound("cleanup deadline exhausted")
        if any(value > 0 for value in signal.getitimer(signal.ITIMER_REAL)):
            raise PairObservationBound("active caller watchdog prevents token cleanup")
        headers = {"Accept": "application/vnd.github+json", "User-Agent": "coordination-pair-token-cleanup",
                   "Authorization": "Bearer " + self.token, "X-GitHub-Api-Version": "2026-03-10"}
        def request_timeout(signum, frame):
            raise TimeoutError("cleanup request time limit")
        previous_handler = signal.signal(signal.SIGALRM, request_timeout)
        signal.setitimer(signal.ITIMER_REAL, min(10, remaining))
        try:
            try:
                response = self.opener.open(urllib.request.Request(url, headers=headers, method=method), timeout=min(10, remaining))
            except urllib.error.HTTPError as error:
                response = error
            with response:
                raw = response.read(1048577)
                if len(raw) > 1048576 or self.clock() >= self.deadline:
                    raise PairObservationBound("cleanup response budget exhausted")
                status = response.code
                # DELETE/401 error bodies are never parsed, hashed, persisted or logged.
                return status, json.loads(raw) if method == "GET" and status == 200 else None
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous_handler)


def pair_policy_mint_observation(logs=None, *, complete=False):
    """Native complete mint logs are a separate join; no elapsed-time reconciliation."""
    if not complete or not isinstance(logs, str):
        return "unknown-action-logs"
    if "Failed to create token" in logs or "attempt" in logs.lower():
        return "unknown-grants-retry-observed"
    return "complete-logs-require-native-source-review"


def cleanup_pair_policy_token(output, *, environment=None, opener=None, clock=time.monotonic):
    env = os.environ if environment is None else environment
    report = {"source": env.get("GITHUB_SHA"), "operation": "observe-execution-pair-policy-21633398",
              "scopedAppTokenUsed": False, "policyObservationOutcome": "unavailable",
              "knownTokenCleanupOutcome": "no-returned-token", "cleanupOutcome": "unknown-grants-held",
              "deleteAcknowledged": False, "sameTokenRejected": False,
              "mintObservation": pair_policy_mint_observation(), "unknownGrantsReconciled": False,
              "publicationAuthorized": False, "publicationEligible": False,
              "defaultPostFallback": "separate-unobserved", "firstCleanupFailure": None,
              "cleanupRequests": []}
    # Read only selected proof flags, never raw policy/error fields into the cleanup report.
    policy = pathlib.Path(output).parent / "policy-observation.json"
    try:
        if policy.stat().st_size <= 1048576:
            proof = json.loads(policy.read_bytes())
            if (isinstance(proof, dict) and proof.get("operation") == report["operation"]
                    and proof.get("source") == report["source"] and proof.get("rulesetId") == 21633398
                    and proof.get("policySha256") == "5182bb35a6dd682cef84ac9ce4210cfd09f38b7410d8c9cf9bae42866bcb81da"):
                report["policyObservationOutcome"] = "complete"
    except (OSError, ValueError, TypeError):
        pass
    token = env.get("PAIR_RULESET_TOKEN", "")
    if token:
        report["scopedAppTokenUsed"] = True
        report["knownTokenCleanupOutcome"] = "incomplete"
        scope_ok = False
        for label, (status, body), error in PairPolicyTokenCleanup(token, opener=opener, clock=clock).sequence():
            report["cleanupRequests"].append({"stage": label, "status": status, "failure": error})
            if label == "scope":
                repos = body.get("repositories") if isinstance(body, dict) else None
                scope_ok = (status == 200 and isinstance(body, dict) and type(body.get("total_count")) is int and body["total_count"] == 1
                            and isinstance(repos, list) and len(repos) == 1 and isinstance(repos[0], dict)
                            and type(repos[0].get("id")) is int and repos[0]["id"] == PAIR_ACCOUNT_OWNER_EVIDENCE["repositoryId"]
                            and repos[0].get("full_name") == "FS-GG/FS.GG.Coordination")
                error = error or (None if scope_ok else "scope-not-established")
            elif label == "delete":
                report["deleteAcknowledged"] = status == 204
                error = error or (None if status == 204 else "delete-not-acknowledged")
            else:
                report["sameTokenRejected"] = status == 401
                error = error or (None if status == 401 else "same-token-rejection-not-established")
            report["cleanupRequests"][-1]["failure"] = error
            if error and report["firstCleanupFailure"] is None:
                report["firstCleanupFailure"] = {"stage": label, "failure": error}
        if scope_ok and report["deleteAcknowledged"] and report["sameTokenRejected"]:
            report["knownTokenCleanupOutcome"] = "acknowledged-and-independently-rejected"
    try:
        pathlib.Path(output).parent.mkdir(parents=True, exist_ok=True)
        pathlib.Path(output).write_bytes(compact(report) + b"\n")
    except OSError:
        print('pair policy token cleanup reporting failed', file=sys.stderr)
        return 1
    return 0 if report["knownTokenCleanupOutcome"] == "acknowledged-and-independently-rejected" else 1


def observe_execution_pair(output, *, read=None, environment=None):
    """Extend release-preflight observations; independent failures never mint eligibility."""
    env = os.environ if environment is None else environment
    candidate = json.loads(json.dumps(PAIR_OBSERVATION_CANDIDATE))
    report = {"schema": "fsgg.coordination.migration-release-preflight/1", "profile": "execution-pair",
              "source": env.get("REQUESTED_PUBLISHER"), "candidate": candidate,
              "proposedVersion": "0.3.0", "publicationAuthorized": False, "versionReserved": False,
              "publicationEligible": False, "namespaceEntitlement": dict(PAIR_ACCOUNT_OWNER_EVIDENCE),
              "trustedPublishingLoginSucceeded": None, "credentialProbe": "not-selected",
              "identity": {"nugetUser": env.get("SELECTED_NUGET_USER"),
                           "repository": "FS-GG/FS.GG.Coordination",
                           "workflow": ".github/workflows/callable-cli-release-publish.yml", "environment": None},
              "observations": [], "firstFailure": None, "collectionComplete": False,
              "exitOutcome": "incomplete", "cleanupOutcome": "no-workload-or-credential-created",
              "reportingOutcome": "pending"}
    def record(name, state, reason=None, reference=None, sha256=None):
        row = {"name": name, "state": state}
        if reason: row["reason"] = reason
        if reference: row["reference"] = reference
        if sha256: row["sha256"] = sha256
        report["observations"].append(row)
        if state not in ("passed", "absent", "observed") and report["firstFailure"] is None:
            report["firstFailure"] = {"name": name, "state": state, "reason": reason}
    publisher = env.get("REQUESTED_PUBLISHER", "")
    valid = (env.get("GITHUB_REPOSITORY") == "FS-GG/FS.GG.Coordination"
             and env.get("GITHUB_REF") == "refs/heads/main" and env.get("GITHUB_RUN_ATTEMPT") == "1"
             and re.fullmatch(r"[0-9a-f]{40}", publisher) is not None and publisher == env.get("GITHUB_SHA")
             and env.get("REQUESTED_SOURCE") == candidate["source"]
             and env.get("REQUESTED_MERGE", "") in ("", candidate["source"])
             and env.get("REQUESTED_SHA256") == candidate["manifestSha256"]
             and env.get("OPERATION", "") == "" and env.get("EXECUTION_PAIR") == "true"
             and env.get("PREFLIGHT_ONLY") == "true" and env.get("CLI_ONLY") == "false"
             and env.get("READBACK_ONLY") == "false"
             and env.get("SELECTED_NUGET_USER") == PAIR_ACCOUNT_OWNER_EVIDENCE["nugetUser"])
    record("inputs", "passed" if valid else "failed", None if valid else "exclusive protected-source/original-A binding refused")
    reader = read if read is not None else PairMetadataReader()
    api = "https://api.github.com/repos/FS-GG/FS.GG.Coordination"
    stopped = False
    def observation(name, url, validate, absent=False, authenticated=True):
        nonlocal stopped
        if not valid or stopped:
            record(name, "blocked" if not valid else "budget-limited", "input binding refused" if not valid else "original collection budget exhausted")
            return
        try:
            status, data, sha = reader(url, authenticated)
            if status == 404:
                state = "absent" if absent else "missing"
            elif status in (401, 403): state = "denied"
            elif status != 200: state = "unknown"
            else: state = validate(data)
            record(name, state, reference=url, sha256=sha)
        except PairObservationBound as error:
            stopped = True
            record(name, "budget-limited", str(error), url)
        except (ValueError, TypeError, KeyError, AttributeError, AssertionError, RecursionError):
            record(name, "malformed", "metadata shape or binding refused", url)
        except (OSError, RuntimeError):
            record(name, "unknown", "metadata acquisition unavailable", url)
    def main_ref(data):
        return "passed" if data["object"]["sha"] == publisher else "foreign"
    def run_metadata(data):
        expected = {"id": candidate["runId"], "head_sha": candidate["source"], "head_branch": "main",
                    "event": "workflow_dispatch", "run_attempt": 1, "status": "completed", "conclusion": "success",
                    "path": ".github/workflows/callable-cli-release-prepare.yml"}
        return "passed" if all(type(data[k]) is type(v) and data[k] == v for k, v in expected.items()) and data["repository"]["id"] == 1346720714 and data["head_repository"]["id"] == 1346720714 else "foreign"
    def jobs_metadata(data):
        rows = data["jobs"]
        if type(data["total_count"]) is not int or data["total_count"] != len(rows) or len(rows) > 100: return "unknown"
        selected = [r for r in rows if r.get("id") == candidate["jobId"]]
        expected = {"id": candidate["jobId"], "run_id": candidate["runId"], "run_attempt": 1,
                    "head_sha": candidate["source"], "name": "Prepare the routine Execution pair without publication",
                    "status": "completed", "conclusion": "success"}
        return "passed" if len(selected) == 1 and all(type(selected[0][k]) is type(v) and selected[0][k] == v for k, v in expected.items()) else "foreign"
    def artifact_metadata(data):
        if data["expired"] is not False: return "expired"
        expected = {"id": candidate["artifactId"], "name": "execution-pair-" + candidate["source"],
                    "size_in_bytes": 169508, "digest": "sha256:" + candidate["archiveSha256"]}
        return "passed" if all(type(data[k]) is type(v) and data[k] == v for k, v in expected.items()) and data["workflow_run"]["id"] == candidate["runId"] and data["workflow_run"]["head_sha"] == candidate["source"] else "foreign"
    observation("main-before", api + "/git/ref/heads/main", main_ref)
    observation("candidate-run", api + f"/actions/runs/{candidate['runId']}", run_metadata)
    observation("candidate-job", api + f"/actions/runs/{candidate['runId']}/attempts/1/jobs?per_page=100", jobs_metadata)
    observation("candidate-artifact", api + f"/actions/artifacts/{candidate['artifactId']}", artifact_metadata)
    for package in sorted(candidate["packages"]):
        namespace = "https://api.github.com/orgs/FS-GG/packages/nuget/" + package.lower()
        def namespace_metadata(data, package=package):
            return "observed" if data["name"].lower() == package.lower() and data["package_type"] == "nuget" and data["owner"]["login"] == "FS-GG" else "foreign"
        observation("namespace-" + package, namespace, namespace_metadata)
        # One finite page: a full page cannot prove absence; no silent pagination truncation.
        def github_versions(data):
            if not isinstance(data, list) or len(data) > 100: return "malformed"
            versions = [row["name"] for row in data]
            if not all(isinstance(v, str) for v in versions): return "malformed"
            return "occupied" if "0.3.0" in versions else "unknown" if len(data) == 100 else "absent"
        observation("github-cell-" + package, namespace + "/versions?per_page=100", github_versions, absent=True)
        def public_versions(data):
            versions = data["versions"]
            if not isinstance(versions, list) or not all(isinstance(v, str) for v in versions): return "malformed"
            return "occupied" if "0.3.0" in versions else "absent"
        observation("nuget-cell-" + package, "https://api.nuget.org/v3-flatcontainer/" + package.lower() + "/index.json", public_versions, absent=True, authenticated=False)
    observation("component-tag", api + "/git/ref/tags/execution/v0.3.0", lambda _: "occupied", absent=True)
    observation("component-release", api + "/releases/tags/execution%2Fv0.3.0", lambda _: "occupied", absent=True)
    def protection(data):
        desired = json.loads((ROOT / "eng/repository-settings/release-tag-ruleset.json").read_bytes())
        keys = ("target", "enforcement", "conditions", "bypass_actors", "rules")
        policy = {key: data[key] for key in keys}
        updates = [row for row in policy["rules"] if row.get("type") == "update"]
        if len(updates) == 1 and set(updates[0]) == {"type"}:
            updates[0]["parameters"] = {"update_allows_fetch_and_merge": False}
        if len(updates) != 1 or set(updates[0]) != {"type", "parameters"} or updates[0]["parameters"] != {"update_allows_fetch_and_merge": False} or updates[0]["parameters"]["update_allows_fetch_and_merge"] is not False: return "foreign"
        return "passed" if type(data["id"]) is int and data["id"] == 21633398 and policy == {key: desired[key] for key in keys} else "foreign"
    observation("component-protection", api + "/rulesets/21633398", protection)
    def github_identity(data):
        login = data["login"]
        if not isinstance(login, str) or re.fullmatch(r"[A-Za-z0-9_.\[\]-]{1,100}", login) is None:
            return "malformed"
        report["identity"]["githubLogin"] = login
        return "observed"
    observation("github-identity", "https://api.github.com/user", github_identity)
    observation("main-after", api + "/git/ref/heads/main", main_ref)
    record("namespace-entitlement", "observed", "account-owner-provided active policy covers first publication of both IDs; current login and every publication gate remain separate", sha256=PAIR_ACCOUNT_OWNER_EVIDENCE["evidenceSha256"])
    required = [row for row in report["observations"] if row["name"] != "namespace-entitlement"]
    report["collectionComplete"] = all(row["state"] in ("passed", "absent", "observed") for row in required)
    report["exitOutcome"] = "collected-no-publication-authority" if report["collectionComplete"] else "incomplete"
    # Report failure propagates independently; it never erases the first collected cause.
    report["reportingOutcome"] = "written"
    try:
        output.write_bytes(compact(report) + b"\n")
    except OSError:
        # Keep first cause and cleanup distinct even when the artifact cannot be written.
        print(compact({"firstFailure": report["firstFailure"], "exitOutcome": report["exitOutcome"],
                       "cleanupOutcome": report["cleanupOutcome"], "reportingOutcome": "failed"}).decode(), file=sys.stderr)
        return 2
    return 0 if report["collectionComplete"] else 1


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--execution-pair-policy-token-cleanup":
        return cleanup_pair_policy_token(pathlib.Path(sys.argv[2]))
    if len(sys.argv) == 3 and sys.argv[1] == "--execution-pair-preflight":
        return observe_execution_pair(pathlib.Path(sys.argv[2]))
    if len(sys.argv) == 5 and sys.argv[1] == "--execution-pair-artifact":
        return extract_execution_pair_artifact(pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3]), pathlib.Path(sys.argv[4]))
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
