#!/usr/bin/env python3
"""Acquire the existing native producer before a workflow receives private source keys.

Root supplies immutable adoption bindings; this adapter only performs bounded
GitHub/file/process IO and exact byte comparisons before invoking those bytes.
Actual publication and adoption are separate operations.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request

sys.dont_write_bytecode = True
sys.path.insert(0,str(Path(__file__).resolve().parent))
from native_io import NativeIOError, capsules, private_directory, safe_environment, write_new

RUNTIME_NAMES = {"FourD.Typed", "FourD.Typed.dll", "FourD.Typed.deps.json", "FourD.Typed.runtimeconfig.json", "FSharp.Core.dll"}
MEMBERS = RUNTIME_NAMES | {"typed-policy-binding.json", "native-runtime-provenance.json"}
ADAPTERS = {"native_bootstrap.py", "native_run.py", "native_io.py", "native_child.py", "native_collect.py", "native_emergency.py", "qualify.py", "source_capsule.py", "seal_native_custody.mjs"}


class GitHubReader:
    def __init__(self, token: str = "", *, cancelled=lambda: False):
        self.token, self.cancelled = token, cancelled

    def request(self, endpoint: str, limit: int, deadline: float, accept="application/vnd.github+json"):
        if not endpoint.startswith("/repos/FS-GG/") or ".." in endpoint or "#" in endpoint:
            raise NativeIOError("native-http-endpoint-refused")
        remaining = deadline - time.monotonic()
        if remaining <= 0 or self.cancelled():
            raise NativeIOError("native-http-deadline-or-cancellation")
        headers = {"Accept": accept, "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "fsgg-fourd-native/1"}
        if endpoint.startswith("/repos/FS-GG/FS.GG.FourD/actions/") and self.token:
            headers["Authorization"] = "Bearer " + self.token
        # Private Actions API observations never follow a redirect with credentials.
        class Redirect(urllib.request.HTTPRedirectHandler):
            count = 0
            def redirect_request(self, request, fp, code, msg, response_headers, newurl):
                from urllib.parse import urlparse
                self.count += 1
                parsed = urlparse(newurl)
                if "Authorization" in request.headers or self.count > 4 or parsed.scheme != "https" or parsed.hostname not in {
                    "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"}:
                    raise NativeIOError("native-http-redirect-refused")
                return super().redirect_request(request, fp, code, msg, response_headers, newurl)
        request = urllib.request.Request("https://api.github.com" + endpoint, headers=headers)
        opener = urllib.request.build_opener(Redirect())
        try:
            response = opener.open(request, timeout=min(15, remaining))
        except urllib.error.HTTPError as error:
            response = error
        with response:
            data = bytearray()
            while len(data) <= limit:
                if time.monotonic() >= deadline or self.cancelled():
                    raise NativeIOError("native-http-deadline-or-cancellation")
                block = response.read(min(65536, limit + 1 - len(data)))
                if not block:
                    return response.status, bytes(data), response.headers.get_all("Link", [])
                data.extend(block)
        raise NativeIOError("native-http-body-bound-refused")


def _sha(path):
    digest = hashlib.sha256()
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_size > 128 * 1024 * 1024:
            raise NativeIOError("native-bootstrap-file-refused")
        for block in iter(lambda: source.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


def acquire_producer(adoption: dict, source: Path, output: Path, *, reader, deadline: float):
    keys = {"schema", "sourceSha", "sourceTree", "releaseId", "assetId", "tag", "name", "bytes", "sha256",
            "authorId", "bindingSha256", "provenanceSha256", "files", "adapterFiles", "hostPath"}
    if set(adoption) != keys or adoption["schema"] != "fsgg.fourd.native-producer-adoption/1":
        raise NativeIOError("native-bootstrap-adoption-shape-refused")
    if set(adoption["files"]) != RUNTIME_NAMES or set(adoption["adapterFiles"]) != ADAPTERS:
        raise NativeIOError("native-bootstrap-closure-refused")
    for name in ("releaseId", "assetId", "bytes", "authorId"):
        if type(adoption[name]) is not int or adoption[name] <= 0:
            raise NativeIOError("native-bootstrap-identifier-refused")
    if adoption["bytes"] > 64 * 1024 * 1024:
        raise NativeIOError("native-bootstrap-size-refused")
    for name, expected in adoption["adapterFiles"].items():
        if _sha(source / "eng/fourd-public-provider" / name) != expected:
            raise NativeIOError("native-bootstrap-adapter-bytes-refused")
    for suffix, expected in (("HEAD", adoption["sourceSha"]), ("HEAD^{tree}", adoption["sourceTree"])):
        result = subprocess.run(["git", "-C", str(source), "rev-parse", suffix], capture_output=True,
                                timeout=min(15, max(0.001, deadline-time.monotonic())), check=True,
                                env=safe_environment(Path("/nonexistent")))
        if result.stdout.decode().strip() != expected:
            raise NativeIOError("native-bootstrap-source-refused")
    status, raw, _ = reader.request(f"/repos/FS-GG/FS.GG.Coordination/releases/{adoption['releaseId']}", 1024*1024, deadline)
    if status != 200:
        raise NativeIOError("native-bootstrap-release-read-refused")
    metadata = capsules.closed_json(raw, 1024*1024)
    assets = [a for a in metadata.get("assets", []) if a.get("id") == adoption["assetId"] or a.get("name") == adoption["name"]]
    if (metadata.get("id") != adoption["releaseId"] or metadata.get("tag_name") != adoption["tag"]
            or metadata.get("author", {}).get("id") != adoption["authorId"] or metadata.get("draft") is not False
            or metadata.get("prerelease") is not False or len(assets) != 1):
        raise NativeIOError("native-bootstrap-release-bytes-refused")
    asset = assets[0]
    if (asset.get("id") != adoption["assetId"] or asset.get("name") != adoption["name"]
            or asset.get("size") != adoption["bytes"] or asset.get("state") != "uploaded"
            or asset.get("uploader", {}).get("id") != adoption["authorId"]
            or asset.get("digest") != "sha256:" + adoption["sha256"]):
        raise NativeIOError("native-bootstrap-asset-bytes-refused")
    status, archive, _ = reader.request(f"/repos/FS-GG/FS.GG.Coordination/releases/assets/{adoption['assetId']}",
                                      adoption["bytes"], deadline, "application/octet-stream")
    if status != 200 or len(archive) != adoption["bytes"] or hashlib.sha256(archive).hexdigest() != adoption["sha256"]:
        raise NativeIOError("native-bootstrap-runtime-bytes-refused")
    private_directory(output)
    write_new(output / "producer-release.json", raw)
    seen = set()
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as package:
        for member in package:
            if (member.name not in MEMBERS or member.name in seen or not member.isfile()
                    or not 0 < member.size <= 32 * 1024 * 1024):
                raise NativeIOError("native-bootstrap-archive-member-refused")
            seen.add(member.name)
            body = package.extractfile(member).read(member.size+1)
            expected = adoption["files"].get(member.name) or adoption[
                "bindingSha256" if member.name == "typed-policy-binding.json" else "provenanceSha256"]
            if len(body) != member.size or hashlib.sha256(body).hexdigest() != expected:
                raise NativeIOError("native-bootstrap-member-bytes-refused")
            write_new(output / member.name, body)
            if member.name == "FourD.Typed":
                (output / member.name).chmod(0o700)
    if seen != MEMBERS:
        raise NativeIOError("native-bootstrap-archive-closure-refused")
    evidence = {"runtimeRoot": str(output), "bindingPath": str(output / "typed-policy-binding.json"),
                "provenancePath": str(output / "native-runtime-provenance.json"), "hostPath": adoption["hostPath"],
                "expectedSourceSha": adoption["sourceSha"], "expectedSourceTree": adoption["sourceTree"],
                "expectedBindingSha256": adoption["bindingSha256"], "expectedProvenanceSha256": adoption["provenanceSha256"],
                "expectedFiles": adoption["files"]}
    request = output / "producer-request.json"
    response = output / "producer-proof.json"
    write_new(request, {"schema": "fsgg.fourd.native-producer-request/1", "producer": evidence})
    # No recipient key or private-source token exists in this bootstrap step.
    result = subprocess.run([str(output / "FourD.Typed"), "native-join", str(request), str(response)],
                            cwd=output, env=safe_environment(Path("/nonexistent"), Path(adoption["hostPath"])),
                            timeout=min(20, max(0.001, deadline-time.monotonic())), check=False,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode or result.stdout or result.stderr:
        raise NativeIOError("native-bootstrap-producer-join-refused")
    write_new(output / "producer-evidence.json", evidence)
    return evidence


def prepare_artifact(source: Path, published: Path, host: Path, archive: Path):
    """Prepare source-qualified bytes for Root's later normal publication/adoption."""
    hashes = {name: _sha(published / name) for name in sorted(RUNTIME_NAMES)}
    def git(value):
        return subprocess.check_output(["git", "-C", str(source), "rev-parse", value], text=True, timeout=15).strip()
    revision, tree = git("HEAD"), git("HEAD^{tree}")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=all"],timeout=15):
        raise NativeIOError("native-artifact-source-dirty")
    binding = {"schema": "fsgg.fourd.typed-policy-binding/1", "sourceSha": revision, "files": hashes}
    binding_raw = capsules.canonical(binding)
    with tempfile.TemporaryDirectory(prefix="native-runtime-info-",dir=archive.parent) as temporary:
        request,response=Path(temporary)/"request.json",Path(temporary)/"response.json"
        write_new(request,{"schema":"fsgg.fourd.native-runtime-info-request/1"})
        subprocess.run([str(published/"FourD.Typed"),"native-runtime-info",str(request),str(response)],
                       env=safe_environment(Path("/nonexistent"),host),timeout=15,check=True,
                       stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        runtime=capsules.closed_json(response.read_bytes(),65536)["runtimeVersion"]
    sdk = subprocess.check_output([str(host), "--version"], cwd=source, env=safe_environment(Path("/nonexistent"),host),
                                  timeout=15, text=True).strip()
    provenance = {"schema": "fsgg.fourd.native-runtime-provenance/1", "sourceSha": revision, "sourceTree": tree,
                  "bindingSha256": hashlib.sha256(binding_raw).hexdigest(), "files": hashes,
                  "sdkVersion": sdk, "runtimeVersion": runtime, "hostSha256": _sha(host)}
    members = {name:(published/name).read_bytes() for name in RUNTIME_NAMES}
    members["typed-policy-binding.json"] = binding_raw
    members["native-runtime-provenance.json"] = capsules.canonical(provenance)
    with archive.open("xb") as destination:
        os.chmod(archive,0o600)
        with tarfile.open(fileobj=destination, mode="w:", format=tarfile.USTAR_FORMAT) as package:
            for name, raw in sorted(members.items()):
                info = tarfile.TarInfo(name);info.size=len(raw);info.mode=0o700 if name=="FourD.Typed" else 0o600
                package.addfile(info,io.BytesIO(raw))
        destination.flush();os.fsync(destination.fileno())
    return {"sourceSha":revision,"sourceTree":tree,"bytes":archive.stat().st_size,"sha256":_sha(archive),
            "bindingSha256":hashlib.sha256(binding_raw).hexdigest(),"provenanceSha256":hashlib.sha256(members["native-runtime-provenance.json"]).hexdigest(),
            "files":hashes,"adapterFiles":{name:_sha(source/"eng/fourd-public-provider"/name) for name in sorted(ADAPTERS)}}


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--adoption",type=Path,required=True)
    parser.add_argument("--source",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--deadline",type=float,required=True)
    args=parser.parse_args()
    adoption=capsules.closed_json(args.adoption.read_bytes(),65536)
    acquire_producer(adoption,args.source,args.output,reader=GitHubReader(),deadline=args.deadline)


if __name__=="__main__":
    main()
