#!/usr/bin/env python3
"""Frozen qualification artifacts and the closed FourD producer release profile.

This publishes no package, performs no native operation, and grants no adoption.
The consumer independently authenticates the eventual public release and runtime.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_bootstrap as bootstrap
import native_runtime_profile as runtime_installation
from native_io import capsules

REPOSITORY = "FS-GG/FS.GG.Coordination"
REPOSITORY_ID = 1346720714
QUALIFIER = ".github/workflows/fourd-typed-source.yml"
PUBLISHER = ".github/workflows/fourd-native-producer-publish.yml"
ARCHIVE = "fourd-native-producer-linux-x64.tar"
MANIFEST = "native-producer-manifest.json"
ARTIFACT_PREFIX = "fourd-native-producer-candidate-"
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_JSON = 1024 * 1024
PRODUCER_KEYS = {"sourceSha", "sourceTree", "bytes", "sha256", "bindingSha256",
                 "provenanceSha256", "files", "adapterFiles"}


class Refused(RuntimeError):
    pass


class Unknown(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise Refused(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def hex_value(value, size=64):
    return isinstance(value, str) and re.fullmatch("[0-9a-f]{" + str(size) + "}", value) is not None


def positive(value):
    return type(value) is int and value > 0


def closed(raw, limit=MAX_JSON):
    try:
        return capsules.closed_json(raw, limit)
    except Exception as error:
        raise Refused("bounded closed JSON refused") from error


def exact(value, keys, message):
    require(isinstance(value, dict) and set(value) == set(keys), message)


def canonical_tar(members):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w:", format=tarfile.USTAR_FORMAT) as package:
        for name, raw in sorted(members.items()):
            info = tarfile.TarInfo(name)
            info.size = len(raw)
            info.mode = 0o700 if name == "FourD.Typed" else 0o600
            package.addfile(info, io.BytesIO(raw))
    return output.getvalue()


def verify_candidate(archive, manifest_raw, *, source=None, tree=None, archive_sha=None,
                     manifest_sha=None, adapters=None):
    require(0 < len(archive) <= MAX_ARCHIVE, "archive byte bound")
    if archive_sha is not None:
        require(hex_value(archive_sha) and sha(archive) == archive_sha, "selected archive digest differs")
    if manifest_sha is not None:
        require(hex_value(manifest_sha) and sha(manifest_raw) == manifest_sha, "selected manifest digest differs")
    manifest = closed(manifest_raw, 65536)
    require(capsules.canonical(manifest) == manifest_raw, "manifest canonical bytes differ")
    exact(manifest, {"schema", "producer"}, "manifest property inventory")
    require(manifest["schema"] == "fsgg.fourd.native-producer-artifact/1", "manifest schema")
    producer = manifest["producer"]
    exact(producer, PRODUCER_KEYS, "producer property inventory")
    require(hex_value(producer["sourceSha"], 40) and hex_value(producer["sourceTree"], 40), "source identity")
    for key in ("sha256", "bindingSha256", "provenanceSha256"):
        require(hex_value(producer[key]), "producer digest")
    require(type(producer["bytes"]) is int and producer["bytes"] == len(archive)
            and producer["sha256"] == sha(archive), "archive identity differs")
    exact(producer["files"], bootstrap.RUNTIME_NAMES, "runtime member inventory")
    exact(producer["adapterFiles"], bootstrap.ADAPTERS, "adapter inventory")
    require(all(hex_value(value) for value in producer["files"].values())
            and all(hex_value(value) for value in producer["adapterFiles"].values()), "closure digest")
    if source is not None:
        require(producer["sourceSha"] == source, "selected source differs")
    if tree is not None:
        require(producer["sourceTree"] == tree, "selected source tree differs")
    if adapters is not None:
        require(producer["adapterFiles"] == adapters, "protected adapter bytes differ")
    members = {}
    try:
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as package:
            for item in package:
                require(item.name in bootstrap.MEMBERS and item.name not in members and item.isfile()
                        and 0 < item.size <= 32 * 1024 * 1024 and not item.pax_headers,
                        "archive regular member inventory")
                require(item.mode == (0o700 if item.name == "FourD.Typed" else 0o600)
                        and item.uid == item.gid == item.mtime == 0
                        and item.uname == item.gname == "", "archive member metadata")
                raw = package.extractfile(item).read(item.size + 1)
                require(len(raw) == item.size, "archive member size")
                members[item.name] = raw
    except (tarfile.TarError, EOFError, OSError) as error:
        raise Refused("archive parser refused") from error
    require(set(members) == bootstrap.MEMBERS and canonical_tar(members) == archive,
            "archive canonical closure differs")
    for name, expected in producer["files"].items():
        require(sha(members[name]) == expected, "runtime bytes differ")
    require(sha(members["typed-policy-binding.json"]) == producer["bindingSha256"]
            and sha(members["native-runtime-provenance.json"]) == producer["provenanceSha256"],
            "binding or provenance bytes differ")
    binding = closed(members["typed-policy-binding.json"], 65536)
    exact(binding, {"schema", "sourceSha", "files"}, "binding property inventory")
    require(binding == {"schema": "fsgg.fourd.typed-policy-binding/1",
                        "sourceSha": producer["sourceSha"], "files": producer["files"]}, "binding differs")
    provenance = closed(members["native-runtime-provenance.json"], 65536)
    exact(provenance, {"schema", "sourceSha", "sourceTree", "bindingSha256", "files",
                       "sdkVersion", "runtimeVersion", "hostSha256"}, "provenance property inventory")
    installed_profile, _ = runtime_installation.profile()
    require(provenance["schema"] == "fsgg.fourd.native-runtime-provenance/1"
            and all(provenance[key] == producer[key] for key in ("sourceSha", "sourceTree", "bindingSha256", "files"))
            and provenance["sdkVersion"] == "10.0.403"
            and provenance["runtimeVersion"] == installed_profile['runtimeVersion']
            and provenance["hostSha256"] == installed_profile['files']['dotnet']['sha256'], "runtime provenance differs")
    return manifest


def prepare(source, published, host, output):
    output.mkdir(mode=0o700)
    producer = bootstrap.prepare_artifact(source, published, host, output / ARCHIVE)
    raw = capsules.canonical({"schema": "fsgg.fourd.native-producer-artifact/1", "producer": producer})
    with (output / MANIFEST).open("xb") as stream:
        stream.write(raw)
    (output / MANIFEST).chmod(0o600)
    verify_candidate((output / ARCHIVE).read_bytes(), raw)
    return producer


def read_pair(directory):
    for name, maximum in ((ARCHIVE, MAX_ARCHIVE), (MANIFEST, 65536)):
        info = (directory / name).lstat()
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= maximum, "candidate regular file bound")
    archive, raw = (directory / ARCHIVE).read_bytes(), (directory / MANIFEST).read_bytes()
    return archive, raw, verify_candidate(archive, raw)


def runtime_profile(archive):
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as package:
        value = closed(package.extractfile("native-runtime-provenance.json").read(65537), 65536)
    return {key: value[key] for key in ("sdkVersion", "runtimeVersion", "hostSha256")}


def unpack_pair(raw):
    require(0 < len(raw) <= MAX_ARCHIVE + MAX_JSON, "artifact zip bound")
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as package:
            rows = package.infolist()
            require(len(rows) == 2 and {r.filename for r in rows} == {ARCHIVE, MANIFEST}, "artifact zip inventory")
            for row in rows:
                require(not row.is_dir() and not row.flag_bits & 1
                        and stat.S_IFMT(row.external_attr >> 16) in (0, stat.S_IFREG)
                        and 0 < row.file_size <= (MAX_ARCHIVE if row.filename == ARCHIVE else 65536),
                        "artifact zip regular file bound")
            return package.read(ARCHIVE), package.read(MANIFEST)
    except (zipfile.BadZipFile, RuntimeError) as error:
        if isinstance(error, Refused):
            raise
        raise Refused("artifact zip parser refused") from error


class API:
    """One bounded request, fixed origins, no retries or credential redirects."""
    def __init__(self, token, deadline):
        self.token, self.deadline = token, deadline

    def check_deadline(self):
        require(time.monotonic() < self.deadline, "original publication deadline exhausted")

    def request(self, method, endpoint, body=None, *, limit=MAX_JSON, binary=False, public=False):
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, "original publication deadline exhausted")
        require((endpoint == "/repos/"+REPOSITORY or endpoint.startswith("/repos/" + REPOSITORY + "/")) and ".." not in endpoint
                and "#" not in endpoint, "publication endpoint refused")
        write = method != "GET"
        require(method in ("GET", "POST", "PATCH") and (not write or self.token), "publication method/credential")
        upload = endpoint.startswith("/repos/" + REPOSITORY + "/releases/") and "/assets?name=" in endpoint
        url = ("https://uploads.github.com" if upload else "https://api.github.com") + endpoint
        raw = body if isinstance(body, bytes) else (capsules.canonical(body) if body is not None else None)
        artifact_zip = method == "GET" and re.fullmatch(
            "/repos/" + re.escape(REPOSITORY) + r"/actions/artifacts/[1-9][0-9]*/zip", endpoint) is not None
        # Actions ZIP downloads negotiate JSON before redirecting to binary storage.
        # Release-asset downloads retain their separate octet-stream negotiation.
        headers = {"Accept": "application/octet-stream" if binary and not artifact_zip else "application/vnd.github+json",
                   "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "fsgg-fourd-native-publication/1"}
        if self.token and not public:
            headers["Authorization"] = "Bearer " + self.token
        if raw is not None:
            headers["Content-Type"] = "application/octet-stream" if upload else "application/json"
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *unused):
                return None
        opener = urllib.request.build_opener(NoRedirect())
        for redirects in range(5):
            request = urllib.request.Request(url, data=raw, headers=headers, method=method)
            try:
                response = opener.open(request, timeout=min(15, max(0.001, self.deadline-time.monotonic())))
            except urllib.error.HTTPError as error:
                response = error
            except (OSError, TimeoutError) as error:
                raise Unknown("publication transport outcome unknown" if write else "publication read unavailable") from error
            with response:
                self.check_deadline()  # The blocking open may return a late positive response.
                if response.status in (301, 302, 303, 307, 308):
                    require(not write and redirects < 4, "publication write/redirect refused")
                    location = response.headers.get("Location", "")
                    parsed = urllib.parse.urlparse(location)
                    require(parsed.scheme == "https" and not parsed.username and not parsed.password
                            and (parsed.hostname in {"github.com", "api.github.com", "release-assets.githubusercontent.com",
                                                     "objects.githubusercontent.com"}
                                 or (parsed.hostname or "").endswith(".blob.core.windows.net")), "publication redirect origin")
                    url = location
                    headers.pop("Authorization", None)
                    continue
                data = bytearray()
                while len(data) <= limit:
                    require(time.monotonic() < self.deadline, "original publication deadline exhausted")
                    try:
                        block = response.read(min(65536, limit+1-len(data)))
                    except (OSError, TimeoutError) as error:
                        raise Unknown("publication response outcome unknown") from error
                    self.check_deadline()  # Includes a late final data block or EOF.
                    if not block:
                        require(not response.headers.get_all("Link", []), "publication census pagination refused")
                        self.check_deadline()
                        return response.status, bytes(data)
                    data.extend(block)
            raise Refused("publication response byte bound")
        raise Refused("publication redirect bound")

    def get(self, endpoint, *, public=False):
        status, raw = self.request("GET", endpoint, public=public)
        require(status == 200, "publication metadata read unavailable")
        value = closed(raw)
        require(isinstance(value, dict), "publication metadata object required")
        return value


class Journal:
    def __init__(self, directory, identity):
        directory.mkdir(mode=0o700, parents=False, exist_ok=True)
        info = directory.lstat()
        require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid()
                and stat.S_IMODE(info.st_mode) == 0o700, "private publication custody required")
        self.directory, self.path = directory, directory / "journal.json"
        require(not self.path.exists() and not self.path.is_symlink(), "existing operation is readback-only")
        self.value = {"schema": "fsgg.fourd.native-publication-journal/1", "identity": identity, "writes": []}
        self.save()

    def save(self):
        raw = capsules.canonical(self.value)
        temporary = self.directory / "journal.next"
        descriptor = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, self.path)
        descriptor = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)

    def possible(self, kind, method, endpoint, body):
        self.value["writes"].append({"kind": kind, "method": method, "endpoint": endpoint,
                                     "requestSha256": sha(body), "state": "possible-send"})
        self.save()  # Must complete before the transport may send anything.

    def confirmed(self, facts):
        self.value["writes"][-1].update(state="confirmed", facts=facts)
        self.save()


def tag_name(source):
    require(hex_value(source, 40), "source SHA required")
    return "fourd-native-producer-" + source


def main_held(api, source):
    value = api.get("/repos/" + REPOSITORY + "/git/ref/heads/main")
    require(value.get("ref") == "refs/heads/main" and value.get("object", {}).get("type") == "commit"
            and value["object"].get("sha") == source, "publication source/main drift")


def absent_publication(api, source):
    tag = tag_name(source)
    for endpoint in ("/git/ref/tags/"+tag, "/releases/tags/"+tag):
        status, _ = api.request("GET", "/repos/"+REPOSITORY+endpoint)
        require(status == 404, "existing tag/release collision or unreadable occupancy")


def save_pair(directory, archive, raw_manifest):
    for name, raw in ((ARCHIVE, archive), (MANIFEST, raw_manifest)):
        descriptor = os.open(directory / name, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())


def qualify(api, identity):
    source, run_id, artifact_id = identity["source"], identity["qualificationRunId"], identity["artifactId"]
    require(hex_value(source, 40) and positive(run_id) and positive(artifact_id)
            and hex_value(identity["archiveSha256"]) and hex_value(identity["manifestSha256"]), "qualification input identity")
    repo = api.get("/repos/" + REPOSITORY)
    require(repo.get("id") == REPOSITORY_ID and repo.get("full_name") == REPOSITORY
            and repo.get("private") is False and repo.get("default_branch") == "main", "public producer repository identity")
    main_held(api, source)
    commit = api.get("/repos/" + REPOSITORY + "/git/commits/" + source)
    tree = commit.get("tree", {}).get("sha")
    require(commit.get("sha") == source and hex_value(tree, 40), "protected source commit identity")
    workflow = api.get("/repos/" + REPOSITORY + "/actions/workflows/" + QUALIFIER.split("/")[-1])
    run = api.get("/repos/" + REPOSITORY + "/actions/runs/" + str(run_id))
    require(positive(workflow.get("id")) and workflow.get("path") == QUALIFIER
            and positive(run.get("id")) and run["id"] == run_id and run.get("workflow_id") == workflow["id"] and run.get("path") == QUALIFIER
            and run.get("head_sha") == source and run.get("head_branch") == "main" and run.get("event") == "push"
            and type(run.get("run_attempt")) is int and run["run_attempt"] == 1
            and run.get("status") == "completed" and run.get("conclusion") == "success"
            and run.get("repository", {}).get("id") == REPOSITORY_ID
            and run.get("repository", {}).get("full_name") == REPOSITORY
            and run.get("head_repository", {}).get("id") == REPOSITORY_ID
            and positive(run.get("check_suite_id")), "qualified original protected-main run required")
    jobs = api.get("/repos/" + REPOSITORY + "/actions/runs/" + str(run_id) + "/attempts/1/jobs?per_page=100")
    require(type(jobs.get("total_count")) is int and jobs["total_count"] == 1
            and isinstance(jobs.get("jobs"), list) and len(jobs["jobs"]) == 1,
            "qualification job population")
    job = jobs["jobs"][0]
    require(positive(job.get("id")) and positive(job.get("run_id")) and job["run_id"] == run_id
            and type(job.get("run_attempt")) is int and job["run_attempt"] == 1
            and job.get("head_sha") == source and job.get("name") == "source-constructor"
            and job.get("status") == "completed" and job.get("conclusion") == "success", "qualification job identity")
    steps = job.get("steps")
    require(isinstance(steps, list) and 2 <= len(steps) <= 20
            and all(isinstance(step, dict) and positive(step.get("number")) for step in steps)
            and [step["number"] for step in steps] == sorted({step["number"] for step in steps}), "qualification step population")
    for name in ("Compile and exercise actual native producer and capsule IO", "Retain the frozen qualified native producer"):
        selected = [step for step in steps if step.get("name") == name]
        require(len(selected) == 1 and selected[0].get("status") == "completed"
                and selected[0].get("conclusion") == "success", "actual qualification step did not succeed")
    check = api.get("/repos/" + REPOSITORY + "/check-runs/" + str(job["id"]), public=True)
    require(check.get("id") == job["id"] and check.get("app", {}).get("id") == 15368
            and check.get("head_sha") == source and check.get("name") == "source-constructor"
            and check.get("status") == "completed" and check.get("conclusion") == "success"
            and positive(check.get("check_suite", {}).get("id"))
            and check["check_suite"]["id"] == run.get("check_suite_id"), "qualification native check provenance")
    artifact = api.get("/repos/" + REPOSITORY + "/actions/artifacts/" + str(artifact_id))
    require(positive(artifact.get("id")) and artifact["id"] == artifact_id and artifact.get("name") == ARTIFACT_PREFIX+source+"-"+str(run_id)+"-1"
            and artifact.get("expired") is False and positive(artifact.get("size_in_bytes"))
            and artifact["size_in_bytes"] <= MAX_ARCHIVE+MAX_JSON
            and positive(artifact.get("workflow_run", {}).get("id")) and artifact["workflow_run"]["id"] == run_id
            and artifact["workflow_run"].get("head_sha") == source
            and artifact["workflow_run"].get("repository_id") == REPOSITORY_ID
            and artifact["workflow_run"].get("head_repository_id") == REPOSITORY_ID
            and isinstance(artifact.get("digest"), str) and artifact["digest"].startswith("sha256:")
            and hex_value(artifact["digest"][7:]), "qualification artifact provenance")
    status, archive_zip = api.request("GET", "/repos/"+REPOSITORY+"/actions/artifacts/"+str(artifact_id)+"/zip",
                                      limit=MAX_ARCHIVE+MAX_JSON, binary=True)
    require(status == 200, "qualification artifact download HTTP status " + str(status))
    require(len(archive_zip) == artifact["size_in_bytes"]
            and sha(archive_zip) == artifact["digest"][7:], "qualification artifact download digest/size")
    archive, raw_manifest = unpack_pair(archive_zip)
    adapters = {}
    for name in sorted(bootstrap.ADAPTERS):
        blob = api.get("/repos/"+REPOSITORY+"/contents/eng/fourd-public-provider/"+name+"?ref="+source)
        require(blob.get("type") == "file" and blob.get("encoding") == "base64"
                and type(blob.get("size")) is int and 0 < blob["size"] <= 128*1024, "protected adapter source")
        try:
            body = base64.b64decode(blob["content"].replace("\n", ""), validate=True)
        except (KeyError, ValueError) as error:
            raise Refused("protected adapter encoding") from error
        require(len(body) == blob["size"] and hashlib.sha1(f"blob {len(body)}\0".encode()+body).hexdigest() == blob.get("sha"),
                "protected adapter Git bytes")
        adapters[name] = sha(body)
    manifest = verify_candidate(archive, raw_manifest, source=source, tree=tree,
                                archive_sha=identity["archiveSha256"], manifest_sha=identity["manifestSha256"], adapters=adapters)
    return archive, raw_manifest, manifest


def release_body(identity):
    return "Qualified FourD native producer; source "+identity["source"]+"; operation "+identity["operationId"]


def public_readback(api, identity, manifest, *, allow_absent=False, original_facts=None):
    source, tag = identity["source"], tag_name(identity["source"])
    original_facts = original_facts or {}

    def original_release(release):
        require(release.get("body") == release_body(identity), "original publication operation differs")
        for key, actual in (("releaseId", release.get("id")), ("authorId", release.get("author", {}).get("id"))):
            require(key not in original_facts or original_facts[key] == actual, "original release identity differs")
    tag_status, tag_raw = api.request("GET", "/repos/"+REPOSITORY+"/git/ref/tags/"+tag, public=True)
    tag_observed = tag_status == 200
    if tag_observed:
        target = closed(tag_raw)
        require(target.get("object", {}).get("type") == "commit" and target["object"].get("sha") == source,
                "immutable public tag target")
    else:
        require(allow_absent and tag_status == 404, "public tag readback unavailable")
    status, raw = api.request("GET", "/repos/"+REPOSITORY+"/releases/tags/"+tag, public=True)
    if allow_absent and status == 404:
        # Public404 may conceal the original draft. It never proves no effect.
        authenticated, original = api.request("GET", "/repos/"+REPOSITORY+"/releases/tags/"+tag)
        if authenticated == 200:
            draft = closed(original)
            require(tag_observed and draft.get("tag_name") == tag and draft.get("target_commitish") == source
                    and positive(draft.get("id")), "original draft readback identity")
            original_release(draft)
            return {"tagObserved": True, "releaseObserved": True, "releaseId": draft["id"], "draft": draft.get("draft"),
                    "publicationObserved": False, "originalRecoveryOnly": True}
        require(authenticated == 404, "original release observation unavailable")
        return {"tagObserved": tag_observed, "releaseObserved": False, "publicationObserved": False, "originalRecoveryOnly": True,
                "absenceDoesNotAuthorizeResend": True}
    require(status == 200, "public release readback unavailable")
    release = closed(raw)
    require(positive(release.get("id")) and release.get("tag_name") == tag
            and release.get("target_commitish") == source and positive(release.get("author", {}).get("id")),
            "public release identity")
    original_release(release)
    require(tag_observed, "public release has no immutable tag")
    assets = release.get("assets")
    require(isinstance(assets, list) and len(assets) <= 20, "public release asset population")
    matches = [a for a in assets if a.get("name") == ARCHIVE]
    if allow_absent and not matches:
        return {"releaseObserved": True, "releaseId": release["id"], "publicationObserved": False}
    require(len(matches) == 1 and len(assets) == 1, "public archive asset inventory")
    asset = matches[0]
    require("assetId" not in original_facts or original_facts["assetId"] == asset.get("id"),
            "original asset identity differs")
    require(release.get("draft") is False and release.get("prerelease") is False
            and positive(asset.get("id")) and asset.get("size") == manifest["producer"]["bytes"]
            and asset.get("digest") == "sha256:"+identity["archiveSha256"] and asset.get("state") == "uploaded"
            and asset.get("uploader", {}).get("id") == release["author"]["id"], "public archive metadata")
    status, body = api.request("GET", "/repos/"+REPOSITORY+"/releases/assets/"+str(asset["id"]),
                               limit=manifest["producer"]["bytes"], binary=True, public=True)
    require(status == 200 and len(body) == manifest["producer"]["bytes"]
            and sha(body) == identity["archiveSha256"], "public archive served bytes")
    verify_candidate(body, capsules.canonical(manifest), source=source, archive_sha=identity["archiveSha256"])
    return {"releaseObserved": True, "publicationObserved": True, "releaseId": release["id"],
            "assetId": asset["id"], "authorId": release["author"]["id"], "tag": tag, "name": ARCHIVE,
            "bytes": asset["size"], "sha256": identity["archiveSha256"]}


def publisher_census(api, identity, environment):
    source, run_id = identity["source"], identity["publisherRunId"]
    require(positive(run_id) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]{0,79}", identity["operationId"]) is not None,
            "publication operation identity")
    require(environment.get("GITHUB_REPOSITORY") == REPOSITORY
            and environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch"
            and environment.get("GITHUB_REF") == "refs/heads/main" and environment.get("GITHUB_RUN_ATTEMPT") == "1"
            and environment.get("GITHUB_SHA") == source
            and environment.get("GITHUB_RUN_ID") == str(run_id)
            and environment.get("GITHUB_WORKFLOW_REF") == REPOSITORY+"/"+PUBLISHER+"@refs/heads/main",
            "original protected publisher required")
    run = api.get("/repos/"+REPOSITORY+"/actions/runs/"+str(run_id))
    title = "FourD producer "+source+" "+identity["operationId"]
    workflow = api.get("/repos/"+REPOSITORY+"/actions/workflows/"+PUBLISHER.split("/")[-1])
    require(positive(run.get("id")) and run["id"] == run_id and run.get("event") == "workflow_dispatch" and run.get("path") == PUBLISHER
            and positive(workflow.get("id")) and workflow.get("path") == PUBLISHER
            and run.get("workflow_id") == workflow["id"] and run.get("repository", {}).get("id") == REPOSITORY_ID
            and run.get("head_repository", {}).get("id") == REPOSITORY_ID
            and run.get("head_sha") == source and run.get("head_branch") == "main"
            and type(run.get("run_attempt")) is int and run["run_attempt"] == 1
            and run.get("status") == "in_progress" and run.get("display_title") == title
            and positive(run.get("actor", {}).get("id")) and positive(run.get("triggering_actor", {}).get("id"))
            and str(run["actor"]["id"]) == environment.get("GITHUB_ACTOR_ID")
            and run.get("triggering_actor", {}).get("id") == run.get("actor", {}).get("id"), "publisher native run identity")
    roster = api.get("/repos/"+REPOSITORY+"/actions/workflows/"+PUBLISHER.split("/")[-1]+"/runs?head_sha="+source+"&per_page=100")
    require(type(roster.get("total_count")) is int and roster["total_count"] == 1
            and isinstance(roster.get("workflow_runs"), list) and len(roster["workflow_runs"]) == 1
            and isinstance(roster["workflow_runs"][0], dict)
            and positive(roster["workflow_runs"][0].get("id")) and roster["workflow_runs"][0]["id"] == run_id,
            "prior source publication operation requires original readback, never replay")


def publish(api, identity, environment, directory):
    publisher_census(api, identity, environment)
    archive, raw_manifest, manifest = qualify(api, identity)
    journal = Journal(directory, identity)
    save_pair(directory, archive, raw_manifest)
    tag = tag_name(identity["source"])
    absent_publication(api, identity["source"])

    def send(kind, method, endpoint, body, expected_status):
        main_held(api, identity["source"])
        qualifier = api.get("/repos/"+REPOSITORY+"/actions/runs/"+str(identity["qualificationRunId"]))
        publisher = api.get("/repos/"+REPOSITORY+"/actions/runs/"+str(identity["publisherRunId"]))
        require(qualifier.get("head_sha") == identity["source"] and type(qualifier.get("run_attempt")) is int
                and qualifier["run_attempt"] == 1 and qualifier.get("status") == "completed"
                and qualifier.get("conclusion") == "success", "qualification changed before write")
        require(publisher.get("head_sha") == identity["source"] and type(publisher.get("run_attempt")) is int
                and publisher["run_attempt"] == 1 and publisher.get("status") == "in_progress",
                "publisher stopped or changed before write")
        raw = body if isinstance(body, bytes) else capsules.canonical(body)
        journal.possible(kind, method, endpoint, raw)
        status, response = api.request(method, endpoint, raw)
        if status != expected_status:
            raise Unknown("publication possible-send requires original readback; no resend")
        try:
            value = closed(response)
            require(isinstance(value, dict), "write response object required")
            return value
        except Refused as error:
            raise Unknown("publication response requires original readback; no resend") from error

    target = send("create-tag", "POST", "/repos/"+REPOSITORY+"/git/refs",
                  {"ref": "refs/tags/"+tag, "sha": identity["source"]}, 201)
    require(target.get("ref") == "refs/tags/"+tag and target.get("object", {}).get("type") == "commit"
            and target["object"].get("sha") == identity["source"], "created tag identity differs")
    journal.confirmed({"tag": tag, "source": identity["source"]})
    target = api.get("/repos/"+REPOSITORY+"/git/ref/tags/"+tag)
    require(target.get("object", {}).get("type") == "commit" and target["object"].get("sha") == identity["source"],
            "created immutable tag target differs")
    release = send("create-draft", "POST", "/repos/"+REPOSITORY+"/releases",
                   {"tag_name": tag, "target_commitish": identity["source"], "name": tag,
                    "draft": True, "prerelease": False, "make_latest": "false",
                    "body": release_body(identity)}, 201)
    require(positive(release.get("id")) and release.get("tag_name") == tag and release.get("draft") is True
            and release.get("prerelease") is False and release.get("target_commitish") == identity["source"]
            and positive(release.get("author", {}).get("id")), "created release identity differs; original readback only")
    journal.confirmed({"releaseId": release["id"], "authorId": release["author"]["id"]})
    target = api.get("/repos/"+REPOSITORY+"/git/ref/tags/"+tag)
    require(target.get("object", {}).get("type") == "commit" and target["object"].get("sha") == identity["source"],
            "created immutable tag target differs")
    current = api.get("/repos/"+REPOSITORY+"/releases/"+str(release["id"]))
    require(current.get("id") == release["id"] and current.get("draft") is True and current.get("tag_name") == tag
            and current.get("author", {}).get("id") == release["author"]["id"]
            and current.get("assets") == [], "archive asset absence required")
    asset = send("upload-archive", "POST", "/repos/"+REPOSITORY+"/releases/"+str(release["id"])
                 +"/assets?"+urllib.parse.urlencode({"name": ARCHIVE}), archive, 201)
    require(positive(asset.get("id")) and asset.get("name") == ARCHIVE and asset.get("size") == len(archive)
            and asset.get("digest") == "sha256:"+sha(archive) and asset.get("state") == "uploaded"
            and asset.get("uploader", {}).get("id") == release["author"]["id"], "uploaded archive identity differs")
    journal.confirmed({"assetId": asset["id"], "sha256": sha(archive)})
    result = send("publish-release", "PATCH", "/repos/"+REPOSITORY+"/releases/"+str(release["id"]),
                  {"draft": False, "make_latest": "false"}, 200)
    require(result.get("id") == release["id"] and result.get("draft") is False and result.get("prerelease") is False,
            "published release identity differs")
    journal.confirmed({"releaseId": release["id"]})
    observation = public_readback(api, identity, manifest, original_facts={
        "releaseId": release["id"], "authorId": release["author"]["id"], "assetId": asset["id"]})
    main_held(api, identity["source"])
    api.check_deadline()
    journal.value["publicReadback"] = observation
    journal.save()
    api.check_deadline()
    return {"producer": manifest["producer"], "publication": observation,
            "runtime": runtime_profile(archive), "adoptionSelected": False}


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    build = sub.add_parser("prepare")
    for name in ("source", "published", "host", "output"):
        build.add_argument("--"+name, type=Path, required=True)
    verify = sub.add_parser("verify")
    verify.add_argument("--directory", type=Path, required=True)
    for command in ("preflight", "publish"):
        send = sub.add_parser(command)
        for name in ("source", "archive-sha256", "manifest-sha256"):
            send.add_argument("--"+name, required=True)
        for name in ("qualification-run-id", "artifact-id"):
            send.add_argument("--"+name, type=int, required=True)
        if command == "publish":
            send.add_argument("--operation-id", required=True)
            send.add_argument("--publisher-run-id", type=int, required=True)
        send.add_argument("--directory", type=Path, required=True)
        send.add_argument("--budget-seconds", type=int, default=540 if command == "publish" else 180)
    recover = sub.add_parser("readback")
    recover.add_argument("--journal", type=Path, required=True)
    recover.add_argument("--candidate", type=Path, required=True)
    recover.add_argument("--budget-seconds", type=int, default=180)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.source, args.published, args.host, args.output)
        return 0
    if args.command == "verify":
        read_pair(args.directory)
        return 0
    require(1 <= args.budget_seconds <= (540 if args.command == "publish" else 180), "finite publication budget")
    token = os.environ.pop("GH_TOKEN", "")
    api = API(token, time.monotonic()+args.budget_seconds)
    if args.command in ("preflight", "publish"):
        identity = {"source": args.source, "qualificationRunId": args.qualification_run_id,
                    "artifactId": args.artifact_id, "archiveSha256": args.archive_sha256,
                    "manifestSha256": args.manifest_sha256}
        if args.command == "publish":
            identity.update(operationId=args.operation_id, publisherRunId=args.publisher_run_id)
            try:
                result = publish(api, identity, dict(os.environ), args.directory)
            except (Refused, Unknown) as error:
                writes = []
                if (args.directory / "journal.json").is_file():
                    writes = closed((args.directory / "journal.json").read_bytes())["writes"]
                result = {"outcome": "unknown" if any(w["state"] == "possible-send" for w in writes)
                          else ("incomplete" if writes else "refused"),
                          "reason": str(error), "operationId": args.operation_id,
                          "writeCount": len(writes), "recovery": "original-readback-only",
                          "adoptionSelected": False}
                print(json.dumps(result, sort_keys=True, separators=(",", ":")))
                return 2
        else:
            archive, raw, manifest = qualify(api, identity)
            absent_publication(api, identity["source"])
            args.directory.mkdir(mode=0o700)
            save_pair(args.directory, archive, raw)
            result = {"candidateVerified": True, "publicationSelected": False, "source": args.source,
                      "sourceTree": manifest["producer"]["sourceTree"], "archiveSha256": args.archive_sha256,
                      "manifestSha256": args.manifest_sha256, "tag": tag_name(args.source), "name": ARCHIVE,
                      "producer": manifest["producer"], "runtime": runtime_profile(archive)}
    else:
        # Recovery has no mutation branch, regardless of journal state or remote outcome.
        info = args.journal.lstat()
        require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= MAX_JSON, "bounded original journal required")
        journal = closed(args.journal.read_bytes())
        require(journal.get("schema") == "fsgg.fourd.native-publication-journal/1", "original journal required")
        archive, raw, manifest = read_pair(args.candidate)
        identity = journal["identity"]
        verify_candidate(archive, raw, source=identity["source"], archive_sha=identity["archiveSha256"],
                         manifest_sha=identity["manifestSha256"])
        facts = {}
        for write in journal["writes"]:
            if write.get("state") == "confirmed":
                facts.update(write.get("facts", {}))
        result = public_readback(api, identity, manifest, allow_absent=True, original_facts=facts)
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (Refused, Unknown) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(2)
