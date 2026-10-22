"""Dormant forward Execution pair release route: policy, observations and effects.

This module is the single source for the guarded publication of a *newly selected*
coherent FS.GG.Execution.Contracts/Processes pair. It never constructs authority:
the shared protected ordered-effect journal engine (pinned in the admission file)
owns durable order, compare-and-swap and recovery; this module supplies only the
fixed thirteen effect identities, their observations, their sends, the fresh
per-effect admission and the invocation-local send fence.

Everything here is import-safe and inert. Live behaviour needs the CLI, an enabled
admission, the pinned engine bytes and separately established credentials. The
original 0.3.0 operation, its admission file and its Unknown reservations are not
read or changed by this route.
"""
from __future__ import annotations

import base64
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import re
import runpy
import stat
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

ENG = pathlib.Path(__file__).resolve().parent
ROOT = ENG.parent

SCHEMA_ADMISSION = "fsgg.coordination.execution-pair-forward-admission/1"
SCHEMA_OPERATION = "fsgg.coordination.execution-pair-forward-operation/1"
SCHEMA_PUBLIC_LOAD = "fsgg.coordination.execution-pair-forward-public-load/1"
SCHEMA_READBACK = "fsgg.coordination.execution-pair-forward-readback/1"
SCHEMA_JOURNAL_ASSET = "fsgg.coordination.execution-pair-forward-publication-journal/1"
PROFILE = "execution-pair-forward"

REPOSITORY = "FS-GG/FS.GG.Coordination"
REPOSITORY_ID = 1346720714
OPERATOR = "EHotwagner"
PUBLISH_WORKFLOW = ".github/workflows/execution-pair-forward-publish.yml"
PREPARE_WORKFLOW = ".github/workflows/callable-cli-release-prepare.yml"
ENVIRONMENT = "execution-pair-forward"
ADMISSION_PATH = "eng/execution-pair-forward-admission.json"
# Everything the publisher executes or reads from the repository. Between the candidate source A
# and any publisher B (including a later resume publisher) these files must be byte-identical,
# except the admission itself; unrelated repository changes cannot strand a partly published pair.
# A control scans the publisher sources and workflow and fails if they read anything outside this set;
# publisher_surface() widens the rule to anything the interpreter or dotnet could resolve by name.
PUBLISHER_CLOSURE = frozenset({
    ADMISSION_PATH, "eng/execution_pair_forward.py", "eng/execution-pair-forward.py",
    "eng/test-callable-cli-installed-harness.py", "eng/write-callable-cli-release-readback.py",
    "eng/repository-settings/release-tag-ruleset.json", "global.json", ".github/workflows/execution-pair-forward-publish.yml",
})

IDS = ("FS.GG.Execution.Contracts", "FS.GG.Execution.Processes")
ORIGINAL_VERSION = "0.3.0"
ORIGINAL_OPERATION = "publish-execution-pair-030-b11fb121-four38484632631"
ORIGINAL_TAG = "execution/v0.3.0"
RULESET_ID = 21633398

ASSET_NAMES = {
    "manifest": "execution-pair-release-manifest.json",
    "consumer-qualification": "execution-pair-consumer-qualification.json",
    "readback": "execution-pair-release-readback.json",
    "publication-journal": "execution-pair-publication-journal.json",
}
GENERATED_ASSETS = ("consumer-qualification", "readback", "publication-journal")

# Exactly thirteen ordered durable effects. Package publication, public package
# execution and finalization are separate credential-isolated jobs; the engine
# journal is shared by all of them.
EFFECT_IDS = (
    "github:FS.GG.Execution.Contracts", "github:FS.GG.Execution.Processes",
    "nuget:FS.GG.Execution.Contracts", "nuget:FS.GG.Execution.Processes",
    "tag", "draft",
    "asset:FS.GG.Execution.Contracts", "asset:FS.GG.Execution.Processes",
    "asset:manifest", "asset:consumer-qualification", "asset:readback", "asset:publication-journal",
    "promote",
)
STAGES = {"packages": EFFECT_IDS[:4], "finalize": EFFECT_IDS}
STAGE_CREDENTIALS = {
    "packages": frozenset({"ledger", "github-packages", "nuget"}),
    "finalize": frozenset({"ledger", "github-contents", "policy-read"}),
}
EFFECT_CREDENTIAL = {**{i: "github-packages" for i in EFFECT_IDS if i.startswith("github:")},
                     **{i: "nuget" for i in EFFECT_IDS if i.startswith("nuget:")},
                     **{i: "github-contents" for i in EFFECT_IDS[4:]}}

# Must equal eng/callable-cli-release.fsx forwardVersionPattern.
FORWARD_VERSION_PATTERN = (r"^(0\.3\.[1-9][0-9]*|0\.([4-9]|[1-9][0-9]+)\.(0|[1-9][0-9]*)"
                           r"|[1-9][0-9]*\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))$")
FORWARD_VERSION = re.compile(FORWARD_VERSION_PATTERN)

SHARED_HELPER_FILES = (
    "scripts/release_successor_execution.py", "scripts/release_successor_journal.py",
    "scripts/release_successor_provider.py", "scripts/release_successor_admission.py",
    "scripts/release-successor-publish.py", "scripts/authority-state-import.py", "scripts/fsgg_feed.py",
)
LIVE_EFFECT_KEYS = ("environment", "ledgerApp", "nugetTrustedPublishing", "occupancyFence", "oldWriterFence", "operationAdmission")

MAX_JSON = 1048576
MAX_PACKAGE = 16777216


class Refused(RuntimeError):
    """A closed, side-effect-free refusal."""


# ---------------------------------------------------------------------------
# Closed primitives


def canonical(value) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode("utf-8")


def sha256_hex(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def strict_json(raw, cap: int = MAX_JSON):
    """Parse bounded JSON with no duplicate keys, constants or trailing bytes."""
    if not isinstance(raw, (bytes, bytearray)) or not 0 < len(raw) <= cap:
        raise Refused("JSON document is absent or exceeds its bound")

    def pairs(items):
        value = {}
        for key, item in items:
            if key in value:
                raise Refused("duplicate JSON key")
            value[key] = item
        return value

    def constant(_):
        raise Refused("non-finite JSON constant")

    try:
        return json.loads(bytes(raw).decode("utf-8"), object_pairs_hook=pairs, parse_constant=constant)
    except (UnicodeError, ValueError) as error:
        raise Refused("JSON document is malformed") from error


def require(condition, reason: str) -> None:
    if not condition:
        raise Refused(reason)


def hexid(value, size: int, what: str) -> str:
    require(type(value) is str and re.fullmatch("[0-9a-f]{%d}" % size, value) is not None and set(value) != {"0"},
            f"{what} must be lowercase non-zero hexadecimal of length {size}")
    return value


def positive(value, what: str) -> int:
    require(type(value) is int and value > 0, f"{what} must be a positive integer")
    return value


def guarded(callable_, *args, reason: str):
    """Run a legacy validator whose failures are asserts, SystemExit or lookup errors."""
    require(sys.flags.optimize == 0, "protected validators require Python optimize=0")
    try:
        return callable_(*args)
    except Refused:
        raise
    except (AssertionError, SystemExit, KeyError, TypeError, ValueError, IndexError, OSError, RuntimeError, zipfile.BadZipFile) as error:
        detail = str(error)[:200] if not isinstance(error, SystemExit) else ""
        raise Refused(f"{reason}{': ' + detail if detail else ''}") from error


# ---------------------------------------------------------------------------
# Version selection


def version_core(version: str) -> tuple[int, int, int]:
    match = re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:[-+][0-9A-Za-z.+-]*)?", version if type(version) is str else "")
    require(match is not None, "observed package version is not a recognizable semantic version")
    return tuple(int(part) for part in match.groups())


def forward_version_allowed(version) -> bool:
    return type(version) is str and FORWARD_VERSION.fullmatch(version) is not None


def select_version(proposed: str, observed_versions, observed_tags=()) -> str:
    """Choose the final version only after authoritative occupancy was observed.

    The result is strictly newer than every observed feed version and tag core,
    including the unchanged original 0.3.0 identity. `proposed` is honoured when
    it already satisfies that rule; otherwise the next patch above the maximum.
    """
    cores = [version_core(v) for v in observed_versions]
    cores += [version_core(t.removeprefix("execution/v")) for t in observed_tags if type(t) is str and t.startswith("execution/v")]
    cores.append(version_core(ORIGINAL_VERSION))
    top = max(cores)
    if forward_version_allowed(proposed) and version_core(proposed) > top:
        return proposed
    chosen = f"{top[0]}.{top[1]}.{top[2] + 1}"
    require(forward_version_allowed(chosen), "no admissible newer version exists")
    return chosen


# ---------------------------------------------------------------------------
# Admission


def _object(value, keys, what):
    require(isinstance(value, dict) and set(value) == set(keys), f"{what} keys differ")
    return value


def _evidence(value, what):
    require(isinstance(value, dict) and {"evidenceSha256", "reference"} <= set(value), f"{what} requires retained evidence")
    hexid(value["evidenceSha256"], 64, what + " evidence")
    require(type(value["reference"]) is str and 0 < len(value["reference"]) <= 200, what + " reference differs")
    return value


def validate_shared_helper(pin) -> dict:
    _object(pin, {"repository", "commit", "files", "boundIntentFence"}, "shared helper pin")
    require(pin["repository"] == "FS-GG/.github", "shared helper repository differs")
    hexid(pin["commit"], 40, "shared helper commit")
    files = pin["files"]
    require(isinstance(files, dict) and set(files) == set(SHARED_HELPER_FILES), "shared helper file set differs")
    for name, digest in files.items():
        hexid(digest, 64, name)
    if pin["boundIntentFence"] is not None:
        _object(pin["boundIntentFence"], {"evidenceSha256", "reference"}, "bound-intent fence")
        _evidence(pin["boundIntentFence"], "bound-intent fence")
    return pin


def validate_selection(selection) -> dict:
    keys = {"operation", "version", "tag", "source", "tree", "operator", "journal", "candidate",
            "consumerQualification", "receiverSources", "namespaceEntitlement", "tagProtection"}
    _object(selection, keys, "forward selection")
    version = selection["version"]
    require(forward_version_allowed(version), "forward version must be a newer stable release than 0.3.0")
    require(selection["tag"] == "execution/v" + version, "forward tag differs from version")
    hexid(selection["source"], 40, "source")
    hexid(selection["tree"], 40, "tree")
    require(type(selection["operation"]) is str
            and re.fullmatch(r"publish-execution-pair-forward-[0-9a-z.-]{1,40}-[0-9a-f]{12}", selection["operation"]) is not None
            and selection["operation"].endswith(selection["source"][:12]) and selection["operation"] != ORIGINAL_OPERATION,
            "forward operation identity differs")
    require(selection["operator"] == OPERATOR, "forward operator differs")
    journal = _object(selection["journal"], {"id"}, "journal selection")
    require(type(journal["id"]) is str and re.fullmatch(r"execution-pair-[0-9a-z.-]{1,40}-[0-9a-f]{12}-[0-9]{2}", journal["id"]) is not None,
            "journal identity differs")
    candidate = _object(selection["candidate"], {"runId", "jobId", "artifactId", "archiveSha256", "manifestSha256", "packages"}, "candidate")
    for key in ("runId", "jobId", "artifactId"):
        positive(candidate[key], key)
    hexid(candidate["archiveSha256"], 64, "archiveSha256")
    hexid(candidate["manifestSha256"], 64, "manifestSha256")
    require(isinstance(candidate["packages"], dict) and set(candidate["packages"]) == set(IDS), "candidate package population differs")
    for digest in candidate["packages"].values():
        hexid(digest, 64, "package sha256")
    q = selection["consumerQualification"]
    _object(q, {"executor", "runId", "workflowSha256", "cases"}, "receiver qualification")
    require(q["executor"] == selection["source"], "forward receivers must run at the exact candidate source")
    receivers = readback_module()["pair_consumer_selection"]
    guarded(receivers, {"consumerQualification": q}, reason="four receiver selection differs")
    require(q["runId"] != candidate["runId"], "receiver run must be separate from candidate preparation")
    # The fixed original S1 receivers pin exactly 0.3.0 with locked restores and cannot receive a
    # newer pair; the selection names its own S1 receiver commits (rollback receivers stay fixed).
    guarded(readback_module()["pair_receiver_table"], selection["receiverSources"], reason="forward receiver sources differ")
    n = _object(selection["namespaceEntitlement"], {"accepted", "packageIds", "nugetUser", "githubRepository", "publisherWorkflow", "evidenceSha256"}, "namespace")
    require(n["accepted"] is True and n["packageIds"] == sorted(IDS) and n["githubRepository"] == REPOSITORY
            and n["publisherWorkflow"] == PUBLISH_WORKFLOW and type(n["nugetUser"]) is str
            and re.fullmatch(r"[A-Za-z0-9_.-]{1,100}", n["nugetUser"]) is not None, "namespace entitlement differs")
    hexid(n["evidenceSha256"], 64, "namespace evidence")
    t = _object(selection["tagProtection"], {"rulesetId", "policySha256"}, "tag protection")
    require(t["rulesetId"] == RULESET_ID, "tag protection ruleset differs")
    hexid(t["policySha256"], 64, "policy hash")
    return selection


def validate_admission(value) -> dict:
    """Strictly validate the admission and return its closed state."""
    _object(value, {"schema", "profile", "enabled", "proposedVersion", "original", "effects", "stages", "sharedHelper", "liveEffects", "selection"}, "forward admission")
    require(value["schema"] == SCHEMA_ADMISSION and value["profile"] == PROFILE, "forward admission identity differs")
    require(type(value["enabled"]) is bool, "enabled must be boolean")
    require(forward_version_allowed(value["proposedVersion"]), "proposed version differs")
    require(value["original"] == {"version": ORIGINAL_VERSION, "operation": ORIGINAL_OPERATION, "tag": ORIGINAL_TAG, "retained": True},
            "original 0.3.0 identity must remain retained and unchanged")
    require(value["effects"] == list(EFFECT_IDS), "forward effect order differs")
    require(value["stages"] == {"packages": list(STAGES["packages"]), "finalize": list(STAGES["finalize"])}, "forward stage windows differ")
    validate_shared_helper(value["sharedHelper"])
    live = value["liveEffects"]
    _object(live, LIVE_EFFECT_KEYS, "live effect prerequisites")
    present = {key: live[key] is not None for key in LIVE_EFFECT_KEYS}
    for key in LIVE_EFFECT_KEYS:
        if live[key] is not None:
            _evidence(live[key], key)
    if live["environment"] is not None:
        require(live["environment"].get("name") == ENVIRONMENT, "protected environment name differs")
    if live["nugetTrustedPublishing"] is not None and selection_user(value) is not None:
        require(live["nugetTrustedPublishing"].get("user") == selection_user(value), "Trusted Publishing user differs from the entitlement")
    selection = value["selection"]
    if selection is not None:
        validate_selection(selection)
        require(selection["version"] != ORIGINAL_VERSION and selection["tag"] != ORIGINAL_TAG, "forward selection must not reuse original 0.3.0")
    if value["enabled"]:
        require(selection is not None, "enabled admission requires a complete selection")
        require(all(present.values()), "enabled admission requires every live-effect prerequisite")
        require(value["sharedHelper"]["boundIntentFence"] is not None, "enabled admission requires the shared bound-intent fence")
    return value


def selection_user(admission: dict):
    selection = admission.get("selection")
    return selection["namespaceEntitlement"]["nugetUser"] if isinstance(selection, dict) else None


def load_admission(path=None) -> dict:
    path = pathlib.Path(path) if path is not None else ROOT / ADMISSION_PATH
    require(path.is_file() and not path.is_symlink(), "forward admission file is absent or unsafe")
    return validate_admission(strict_json(path.read_bytes()))


def effective_state(admission: dict) -> str:
    """disabled (no selection), selected (reviewable, no effects), or enabled."""
    if admission["enabled"]:
        return "enabled"
    return "selected" if admission["selection"] is not None else "disabled"


def selection_as_pair_admission(selection: dict) -> dict:
    """The existing pair-admission shape consumed by the maintained validators."""
    c = selection["candidate"]
    return {"schema": "fsgg.coordination.execution-pair-admission/1", "profile": "execution-pair", "enabled": False,
            "operation": selection["operation"], "version": selection["version"], "tag": selection["tag"],
            "source": selection["source"], "tree": selection["tree"], "protectedMerge": selection["source"],
            "runId": c["runId"], "jobId": c["jobId"], "artifactId": c["artifactId"], "archiveSha256": c["archiveSha256"],
            "manifestSha256": c["manifestSha256"], "packages": dict(c["packages"]),
            "namespaceEntitlement": selection["namespaceEntitlement"], "tagProtection": selection["tagProtection"],
            "consumerQualification": selection["consumerQualification"]}


_EXECUTABLE_SURFACE_NAMES = frozenset({"nuget.config", "global.json", "directory.build.props", "directory.build.targets", "directory.packages.props",
                                       "sitecustomize.py", "usercustomize.py", "dotnet-tools.json"})


def publisher_surface(path: str) -> bool:
    """Paths that can change what the publisher runs: its closure and anything Python or dotnet could resolve by name.

    The publisher is also run hermetically (`python3 -I`, dotnet from a clean directory), so this is
    defense in depth: shadowing modules, import hooks and tool configuration are never a free change.
    """
    if path in PUBLISHER_CLOSURE:
        return True
    directory, _, name = path.rpartition("/")
    lowered = name.lower()
    return (lowered in _EXECUTABLE_SURFACE_NAMES or lowered.endswith(".pth")
            or (lowered.endswith(".py") and directory in ("", "eng")) or lowered.endswith((".config", ".props", ".targets")))


def publication_delta_allowed(paths) -> bool:
    """Between A and any publisher B the publisher surface may differ only in the admission file."""
    paths = list(paths)
    return all(type(p) is str for p in paths) and {p for p in paths if publisher_surface(p)} <= {ADMISSION_PATH}


def bind_publisher(selection: dict, publisher_sha: str, diff_paths, *, ancestor: bool, tree: str) -> None:
    """Candidate A must be an ancestor of publisher B; B may change only the admission within the publisher closure."""
    hexid(publisher_sha, 40, "publisher")
    require(ancestor is True, "candidate source is not an ancestor of the publisher")
    require(tree == selection["tree"], "candidate source tree differs from the selection")
    require(publication_delta_allowed(diff_paths), "candidate-to-publisher delta changes the publisher closure beyond the admission")


# ---------------------------------------------------------------------------
# Shared helper pin and engine


def helper_digest_report(root: pathlib.Path, pin: dict) -> dict:
    root = pathlib.Path(root)
    report = {}
    for name, expected in pin["files"].items():
        path = root / name
        require(path.is_file() and not path.is_symlink(), f"shared helper {name} is absent or unsafe")
        raw = path.read_bytes()
        require(len(raw) <= 8 * 1024 * 1024 and sha256_hex(raw) == expected, f"shared helper {name} differs from its pinned bytes")
        report[name] = expected
    return report


def fetch_shared_helper(pin: dict, destination: pathlib.Path, opener=None) -> pathlib.Path:
    """Materialize exactly the pinned immutable bytes from the pinned commit."""
    destination = pathlib.Path(destination)
    destination.mkdir(parents=True, exist_ok=True)
    for name, expected in pin["files"].items():
        url = f"https://raw.githubusercontent.com/{pin['repository']}/{pin['commit']}/{name}"
        request = urllib.request.Request(url, headers={"User-Agent": "fsgg-execution-pair-forward"})
        opener = opener or urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(request, timeout=60) as response:
            raw = response.read(8 * 1024 * 1024 + 1)
        require(0 < len(raw) <= 8 * 1024 * 1024 and sha256_hex(raw) == expected, f"fetched shared helper {name} differs from its pinned bytes")
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(raw)
    return destination


class Engine:
    """Namespace over the exact pinned shared journal engine modules."""

    def __init__(self, root):
        self.root = pathlib.Path(root)
        scripts = str(self.root / "scripts")
        if scripts not in sys.path:
            sys.path.insert(0, scripts)
        import release_successor_execution as execution  # noqa: PLC0415
        import release_successor_journal as journal  # noqa: PLC0415
        import release_successor_provider as provider  # noqa: PLC0415
        self.execution, self.journal, self.provider = execution, journal, provider
        self.Effect, self.Observation, self.Dispatch = execution.Effect, execution.Observation, execution.Dispatch
        self.EngineRefused = execution.Refused
        spec = importlib.util.spec_from_file_location("execution_pair_ordinary_route", self.root / "scripts/release-successor-publish.py")
        require(spec is not None and spec.loader is not None, "ordinary route source unavailable")
        self.route = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.route)


def load_engine(root, pin: dict) -> Engine:
    helper_digest_report(root, pin)
    return Engine(root)


# ---------------------------------------------------------------------------
# Candidate


def payload_map(archive: bytes) -> dict:
    """Name to sha256 for every member except the repository signature."""
    try:
        return _payload_map(archive)
    except (zipfile.BadZipFile, zipfile.LargeZipFile, NotImplementedError, RuntimeError, ValueError, EOFError, OSError) as error:
        if isinstance(error, Refused):
            raise
        raise Refused("package archive is not a readable zip") from error


def _payload_map(archive: bytes) -> dict:
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        names = z.namelist()
        require(len(names) == len(set(names)) and 0 < len(names) <= 256, "package member population differs")
        total = 0
        for info in z.infolist():
            total += info.file_size
            parts = info.filename.split("/")
            require(not info.filename.startswith("/") and "\\" not in info.filename and ".." not in parts and not info.flag_bits & 1
                    and info.file_size <= 8388608 and stat.S_IFMT(info.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR),
                    "unsafe package member")
        require(total <= 33554432, "expanded package exceeds its bound")
        return {name: sha256_hex(z.read(name)) for name in sorted(names) if name != ".signature.p7s"}


def payload_digest(archive: bytes) -> str:
    return "sha256:" + sha256_hex(canonical(payload_map(archive)))


class Candidate:
    def __init__(self, directory, selection):
        directory = pathlib.Path(directory)
        version = selection["version"]
        expected = {f"{i}.{version}.nupkg" for i in IDS} | {ASSET_NAMES["manifest"]}
        require(directory.is_dir() and {p.name for p in directory.iterdir()} == expected
                and all(p.is_file() and not p.is_symlink() for p in directory.iterdir()), "candidate directory population differs")
        self.directory = directory
        self.version = version
        self.manifest_bytes = (directory / ASSET_NAMES["manifest"]).read_bytes()
        require(sha256_hex(self.manifest_bytes) == selection["candidate"]["manifestSha256"], "candidate manifest digest differs")
        manifest = strict_json(self.manifest_bytes)
        require(manifest.get("schema") == "fsgg.coordination.execution-pair-release-preparation/1" and manifest.get("profile") == "execution-pair"
                and manifest.get("version") == version and manifest.get("tag") == selection["tag"]
                and manifest.get("sourceCommit") == selection["source"] and manifest.get("sourceTree") == selection["tree"]
                and manifest.get("publicationAuthorized") is False and manifest.get("tagAuthorized") is False
                and (manifest.get("ownership") or {}).get("reservation") == "unreserved"
                and isinstance(manifest.get("packages"), list) and len(manifest["packages"]) == 2, "candidate manifest identity differs")
        self.packages, self.assemblies, self.bytes, self.payloads = {}, {}, {}, {}
        for row in manifest["packages"]:
            require(row.get("packageId") in IDS and row["packageId"] not in self.packages and row.get("file") == f"{row['packageId']}.{version}.nupkg",
                    "candidate member identity differs")
            raw = (directory / row["file"]).read_bytes()
            require(0 < len(raw) <= MAX_PACKAGE and sha256_hex(raw) == row["sha256"] == selection["candidate"]["packages"][row["packageId"]]
                    and row.get("bytes") == len(raw), "candidate package bytes differ from the selection")
            hexid(row.get("assemblySha256"), 64, "assembly digest")
            self.packages[row["packageId"]] = row["sha256"]
            self.assemblies[row["packageId"]] = row["assemblySha256"]
            self.bytes[row["packageId"]] = raw
            self.payloads[row["packageId"]] = payload_digest(raw)
        require(set(self.packages) == set(IDS), "candidate package population differs")


def content_id(selection: dict) -> str:
    """Identity of the immutable inputs; generated release assets do not enter it."""
    q = selection["consumerQualification"]
    descriptor = {"schema": SCHEMA_OPERATION, "operation": selection["operation"], "version": selection["version"], "tag": selection["tag"],
                  "source": selection["source"], "tree": selection["tree"], "journal": selection["journal"]["id"],
                  "candidate": selection["candidate"], "receivers": sha256_hex(canonical(q)),
                  "receiverSources": sha256_hex(canonical(selection["receiverSources"])),
                  "tagProtection": selection["tagProtection"], "nugetUser": selection["namespaceEntitlement"]["nugetUser"]}
    return "sha256:" + sha256_hex(canonical(descriptor))


def journal_ref(selection: dict) -> str:
    return "refs/heads/fsgg/v2/journal/release/" + selection["journal"]["id"]


def journal_intent(selection: dict) -> dict:
    return {"contentId": content_id(selection), "sourceSha": selection["source"], "version": selection["version"],
            "candidateArchiveSha256": selection["candidate"]["archiveSha256"], "operator": selection["operator"]}


def asset_name(effect_identity: str, version: str) -> str:
    kind = effect_identity.removeprefix("asset:")
    if kind in IDS:
        return f"{kind}.{version}.nupkg"
    return ASSET_NAMES[kind]


def plan_effects(engine, selection: dict, candidate: Candidate, generated: dict | None = None, stage: str = "finalize"):
    """Return the engine Effect tuple for the stage window (generated digests only when required)."""
    require(stage in STAGES, "unknown stage")
    cid = content_id(selection)
    effects = []
    for identity in STAGES[stage]:
        kind, _, package = identity.partition(":")
        if kind in ("github", "nuget"):
            effects.append(engine.Effect(identity, candidate.payloads[package], candidate.packages[package]))
        elif identity == "tag":
            effects.append(engine.Effect(identity, selection["source"], cid))
        elif identity in ("draft", "promote"):
            effects.append(engine.Effect(identity, cid, cid))
        elif package in IDS:
            effects.append(engine.Effect(identity, candidate.packages[package], candidate.packages[package]))
        elif package == "manifest":
            digest = sha256_hex(candidate.manifest_bytes)
            effects.append(engine.Effect(identity, digest, digest))
        else:
            require(package in GENERATED_ASSETS and generated is not None and package in generated,
                    "generated release asset digest unavailable before its inputs")
            digest = sha256_hex(generated[package])
            effects.append(engine.Effect(identity, digest, digest))
    require(len(effects) == len(STAGES[stage]) and [e.identity for e in effects] == list(STAGES[stage]), "effect plan differs")
    return tuple(effects)


# ---------------------------------------------------------------------------
# Strict occupancy and policy joins


def parse_versions(raw: bytes | None) -> list:
    """A flat-container index; absence is an empty list, anything else must be exact."""
    if raw is None:
        return []
    value = strict_json(raw)
    require(isinstance(value, dict) and set(value) == {"versions"} and isinstance(value["versions"], list)
            and len(value["versions"]) <= 5000 and all(type(v) is str for v in value["versions"]), "feed version index differs")
    for version in value["versions"]:
        version_core(version)
    return list(value["versions"])


def feed_url(feed: str, package: str, version: str | None = None) -> str:
    lower = package.lower()
    if feed == "github":
        base = f"https://nuget.pkg.github.com/fs-gg/download/{lower}"
    else:
        require(feed == "nuget", "unknown feed")
        base = f"https://api.nuget.org/v3-flatcontainer/{lower}"
    return base + "/index.json" if version is None else f"{base}/{version}/{lower}.{version}.nupkg"


def fetch_feed(transport, github_token: str, version: str, feed: str, package: str):
    """One bounded read of a served package; None only for an authentic 404."""
    headers = {}
    if feed == "github":
        headers["Authorization"] = "Basic " + base64.b64encode(f"x:{github_token}".encode()).decode()
    status, raw = transport.get(feed_url(feed, package, version), headers=headers, cap=MAX_PACKAGE)
    if status == 404:
        return None
    require(status == 200, f"{feed} observation returned HTTP {status}")
    return raw


def observe_occupancy(transport, selection: dict, github_token: str) -> dict:
    """Bounded read-only observation of all four package cells, version lists, tag and release."""
    version = selection["version"]
    observed = {"versions": {}, "cells": {}, "listing": {}, "tags": None, "tag": None, "release": None}
    github_headers = {"Authorization": "Basic " + base64.b64encode(f"x:{github_token}".encode()).decode()}
    api = {"Authorization": f"Bearer {github_token}", "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}
    for feed in ("github", "nuget"):
        headers = github_headers if feed == "github" else {}
        for package in IDS:
            key = f"{feed}:{package}"
            if feed == "github":
                # One finite page: a full page cannot prove absence.
                status, raw = transport.get(f"https://api.github.com/orgs/FS-GG/packages/nuget/{package.lower()}/versions?per_page=100", headers=api, cap=MAX_JSON)
                require(status in (200, 404), f"{key} version listing returned HTTP {status}")
                observed["listing"][key] = status
                rows = strict_json(raw) if status == 200 else []
                require(isinstance(rows, list) and len(rows) < 100 and all(isinstance(r, dict) and type(r.get("name")) is str for r in rows), f"{key} version listing is incomplete")
                for row in rows:
                    version_core(row["name"])
                observed["versions"][key] = [r["name"] for r in rows]
            else:
                status, raw = transport.get(feed_url(feed, package), headers=headers, cap=MAX_JSON)
                require(status in (200, 404), f"{key} version index returned HTTP {status}")
                observed["listing"][key] = status
                observed["versions"][key] = parse_versions(raw if status == 200 else None)
            status, _ = transport.get(feed_url(feed, package, version), headers=headers, cap=MAX_PACKAGE)
            require(status in (200, 404), f"{key} cell returned HTTP {status}")
            observed["cells"][key] = status
    status, raw = transport.get(f"https://api.github.com/repos/{REPOSITORY}/git/matching-refs/tags/execution/v", headers=api, cap=MAX_JSON)
    require(status == 200, f"tag listing returned HTTP {status}")
    rows = strict_json(raw)
    require(isinstance(rows, list) and len(rows) < 100 and all(isinstance(r, dict) and type(r.get("ref")) is str for r in rows), "tag listing is incomplete")
    observed["tags"] = [r["ref"].removeprefix("refs/tags/") for r in rows]
    status, _ = transport.get(f"https://api.github.com/repos/{REPOSITORY}/git/ref/tags/{selection['tag']}", headers=api, cap=MAX_JSON)
    require(status in (200, 404), f"tag observation returned HTTP {status}")
    observed["tag"] = status
    status, _ = transport.get(f"https://api.github.com/repos/{REPOSITORY}/releases/tags/{urllib.parse.quote(selection['tag'], safe='')}", headers=api, cap=MAX_JSON)
    require(status in (200, 404), f"release observation returned HTTP {status}")
    observed["release"] = status
    return observed


def occupancy_join(selection: dict, observed: dict) -> dict:
    """Refuse unless the selected version is newer than everything observed and wholly unoccupied."""
    version = selection["version"]
    keys = {f"{feed}:{package}" for feed in ("github", "nuget") for package in IDS}
    require(set(observed.get("versions", {})) == keys and set(observed.get("cells", {})) == keys and set(observed.get("listing", {})) == keys,
            "occupancy observation is incomplete")
    require(observed.get("tag") == 404 and observed.get("release") == 404, "component tag or release already exists")
    require(all(status == 404 for status in observed["cells"].values()), "a selected package cell is already populated")
    # A GitHub Packages 404 can mean "not visible to this token" as well as "absent": it is trusted only
    # when the public feed also shows no history for that package.
    for package in IDS:
        require(not (observed["listing"][f"github:{package}"] == 404 and observed["versions"][f"nuget:{package}"]),
                f"GitHub Packages listing for {package} is ambiguous (404 while the public feed has history)")
    everything = [v for versions in observed["versions"].values() for v in versions]
    require(version not in everything, "selected version already appears in a feed index")
    chosen = select_version(version, everything, observed["tags"])
    require(chosen == version, f"selected version is not newer than observed occupancy; admissible: {chosen}")
    return {"version": version, "observedVersions": sorted(set(everything), key=version_core), "cells": "absent", "tag": "absent", "release": "absent"}


def harness_module() -> dict:
    return runpy.run_path(str(ENG / "test-callable-cli-installed-harness.py"))


def readback_module() -> dict:
    return runpy.run_path(str(ENG / "write-callable-cli-release-readback.py"))


def policy_join(raw: bytes, selection: dict) -> dict:
    """The one maintained complete tag-policy predicate, shared with the original route."""
    desired = strict_json((ROOT / "eng/repository-settings/release-tag-ruleset.json").read_bytes())
    return guarded(harness_module()["pair_protection"], strict_json(raw), selection_as_pair_admission(selection), desired,
                   reason="tag protection policy join differs")


# ---------------------------------------------------------------------------
# Public load gate and deterministic release assets


def validate_public_load(raw: bytes, selection: dict, candidate: Candidate) -> dict:
    receipt = strict_json(raw)
    _object(receipt, {"schema", "contentId", "operation", "version", "source", "candidateArchiveSha256", "packages", "installedLoad"}, "public-load receipt")
    require(receipt["schema"] == SCHEMA_PUBLIC_LOAD and receipt["contentId"] == content_id(selection) and receipt["operation"] == selection["operation"]
            and receipt["version"] == selection["version"] and receipt["source"] == selection["source"]
            and receipt["candidateArchiveSha256"] == selection["candidate"]["archiveSha256"], "public-load receipt identity differs")
    expected = [{"packageId": i, "candidateSha256": candidate.packages[i], "payloadDigest": candidate.payloads[i]} for i in IDS]
    rows = receipt["packages"]
    require(isinstance(rows, list) and len(rows) == 2 and [{k: r.get(k) for k in ("packageId", "candidateSha256", "payloadDigest")} for r in rows] == expected
            and all(set(r) == {"packageId", "candidateSha256", "payloadDigest", "servedArchiveSha256", "signature"} and r["signature"] in ("absent", "verified") for r in rows), "public-load package join differs")
    for row in rows:
        hexid(row["servedArchiveSha256"], 64, "served archive")
    installed = receipt["installedLoad"]
    require(isinstance(installed, dict) and installed.get("schema") == "fsgg.coordination.execution-pair-installed-load/1" and installed.get("mode") == "public"
            and installed.get("publicOnly") is True and installed.get("producerProjectReferenced") is False and installed.get("packageInputsOnly") is True
            and installed.get("privateCachesUnavailable") is True and installed.get("effectiveProjectIsolationGuard") is True
            and installed.get("effectiveNuGetFoldersVerified") is True and installed.get("actualCompilerReferencesVerified") is True
            and installed.get("nativeFixtureInvoked") is False and installed.get("consumerAdoptionAccepted") is False
            and installed.get("physicalSourceExclusionEstablished") is False, "public installed-load proof is incomplete")
    require(installed.get("packages") == [{"packageId": i, "version": selection["version"], "runtimeSha256": candidate.assemblies[i]} for i in IDS],
            "public installed runtime differs from the candidate payload")
    return receipt


def build_public_load_receipt(selection: dict, candidate: Candidate, served: dict, signatures: dict, installed: dict) -> bytes:
    rows = [{"packageId": i, "candidateSha256": candidate.packages[i], "payloadDigest": candidate.payloads[i],
             "servedArchiveSha256": sha256_hex(served[i]), "signature": signatures[i]} for i in IDS]
    return canonical({"schema": SCHEMA_PUBLIC_LOAD, "contentId": content_id(selection), "operation": selection["operation"],
                      "version": selection["version"], "source": selection["source"],
                      "candidateArchiveSha256": selection["candidate"]["archiveSha256"], "packages": rows, "installedLoad": installed}) + b"\n"


def feed_fact(feed: str, candidate_sha: str, served: bytes, candidate: bytes) -> dict:
    """Exact retained-byte verification: GitHub is byte-exact, nuget.org may add only its signature."""
    if feed == "github":
        require(served == candidate, "GitHub Packages archive differs from the retained candidate")
    require(payload_map(served) == payload_map(candidate), f"{feed} package payload differs from the retained candidate")
    return {"feed": "github-packages" if feed == "github" else "nuget-org", "candidateSha256": candidate_sha,
            "servedArchiveSha256": sha256_hex(served), "payloadMatch": True}


def build_assets(selection: dict, candidate: Candidate, *, receivers: dict, public_load: bytes, feeds: dict) -> dict:
    """Deterministic release evidence derived only from retained, authenticated inputs."""
    cid = content_id(selection)
    receipt = validate_public_load(public_load, selection, candidate)
    consumer = canonical(receivers) + b"\n"
    members = []
    for package in IDS:
        members.append({"packageId": package, "candidateSha256": candidate.packages[package], "assemblySha256": candidate.assemblies[package],
                        "githubPackages": feeds[f"github:{package}"], "nugetOrg": feeds[f"nuget:{package}"]})
    readback = canonical({"schema": SCHEMA_READBACK, "profile": PROFILE, "operation": selection["operation"], "contentId": cid,
                          "source": selection["source"], "sourceTree": selection["tree"], "version": selection["version"], "tag": selection["tag"],
                          "manifestSha256": sha256_hex(candidate.manifest_bytes), "candidateArchiveSha256": selection["candidate"]["archiveSha256"],
                          "packages": members, "installedLoad": receipt["installedLoad"],
                          "consumerQualificationSha256": sha256_hex(consumer), "publicLoadReceiptSha256": sha256_hex(public_load),
                          "consumerAdoptionAccepted": False, "originalOperationUnchanged": ORIGINAL_OPERATION}) + b"\n"
    journal = canonical({"schema": SCHEMA_JOURNAL_ASSET, "operation": selection["operation"], "contentId": cid, "journal": selection["journal"]["id"],
                         "effects": list(EFFECT_IDS), "feeds": {k: feeds[k] for k in sorted(feeds)}}) + b"\n"
    return {"consumer-qualification": consumer, "readback": readback, "publication-journal": journal}


# ---------------------------------------------------------------------------
# Transport


class _NoCredentialRedirect(urllib.request.HTTPRedirectHandler):
    """Follow https redirects but never carry credentials to another origin."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        target = urllib.parse.urlsplit(newurl)
        if target.scheme != "https":
            raise urllib.error.HTTPError(newurl, code, "redirect to non-https refused", headers, fp)
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None and urllib.parse.urlsplit(req.full_url).netloc != target.netloc:
            for name in list(new.headers):
                if name.lower() == "authorization":
                    del new.headers[name]
            new.unredirected_hdrs.pop("Authorization", None)
        return new


class UrllibTransport:
    """One bounded GET per call. No retry, no fallback origin, no credentials after redirects."""

    def get(self, url: str, *, headers=None, cap: int = MAX_JSON, timeout: int = 60):
        require(urllib.parse.urlsplit(url).scheme == "https", "transport requires https")
        request = urllib.request.Request(url, headers={"User-Agent": "fsgg-execution-pair-forward", **(headers or {})})
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoCredentialRedirect)
        try:
            with opener.open(request, timeout=timeout) as response:
                raw = response.read(cap + 1)
                status = response.status
        except urllib.error.HTTPError as error:
            return error.code, b""
        require(len(raw) <= cap, "response exceeds its bound")
        return status, raw


# ---------------------------------------------------------------------------
# Send fence and admission


class SendFence:
    """Invocation-local, shared by the journal and provider wrappers.

    A provider send is legal only for the effect whose `intent` this very invocation
    just wrote through the journal, and at most once. An intent found in the journal
    from any earlier invocation is never a send permit.
    """

    def __init__(self):
        self.armed = None
        self.spent = set()
        self.sends = []

    def arm(self, effect: str) -> None:
        require(self.armed is None, "a prior intent in this invocation was never dispatched")
        self.armed = effect

    def consume(self, effect: str) -> None:
        require(self.armed == effect and effect not in self.spent, "send fence: no intent written by this invocation permits a send")
        self.spent.add(effect)
        self.armed = None
        self.sends.append(effect)


class FencedJournal:
    def __init__(self, journal, fence: SendFence):
        self._journal, self._fence = journal, fence

    def __getattr__(self, name):
        return getattr(self._journal, name)

    def read(self):
        return self._journal.read()

    def compare_and_swap(self, expected, effect, state):
        won = self._journal.compare_and_swap(expected, effect, state)
        if won and state == "intent":
            self._fence.arm(effect)
        return won


class FencedProvider:
    def __init__(self, provider, fence: SendFence):
        self._provider, self._fence = provider, fence

    def observe(self, effect):
        return self._provider.observe(effect)

    def dispatch(self, effect):
        self._fence.consume(effect.identity)
        return self._provider.dispatch(effect)


class PairAdmission:
    """Fresh native admission before every intent, dispatch and settlement."""

    def __init__(self, api, selection, effects, *, stage, publisher_sha, run_id, actor, ref, policy_reader=None):
        require(stage in STAGES, "unknown stage")
        require(ref == "refs/heads/main" and actor == selection["operator"], "forward publisher caller or branch differs")
        self.api, self.selection, self.stage = api, selection, stage
        self.publisher_sha, self.run_id = publisher_sha, run_id
        self.content_id = content_id(selection)
        self.requests = {e.identity: e.request_digest for e in effects}
        self.requests["journal"] = self.content_id
        self.window = set(STAGES[stage])
        self.policy_reader = policy_reader

    def authorize(self, content_id_: str, effect: str, action: str, request_digest: str) -> bool:
        if (content_id_ != self.content_id or action not in {"intent", "dispatch", "settle"}
                or self.requests.get(effect) != request_digest):
            return False
        if effect != "journal" and effect not in self.window:
            return False  # Never touch an effect outside this stage's window.
        if effect != "journal" and action in {"intent", "dispatch"} and EFFECT_CREDENTIAL[effect] not in STAGE_CREDENTIALS[self.stage]:
            return False  # Never write an intent this stage holds no credential to send.
        repository = self.api.get(f"repos/{REPOSITORY}")
        if repository.get("id") != REPOSITORY_ID or repository.get("full_name") != REPOSITORY:
            return False
        run = self.api.get(f"repos/{REPOSITORY}/actions/runs/{self.run_id}")
        if (run.get("id") != self.run_id or run.get("repository", {}).get("id") != REPOSITORY_ID
                or run.get("path") != PUBLISH_WORKFLOW or run.get("event") != "workflow_dispatch"
                or run.get("head_branch") != "main" or run.get("head_sha") != self.publisher_sha
                or run.get("run_attempt") != 1 or run.get("actor", {}).get("login") != self.selection["operator"]
                or run.get("status") != "in_progress"):
            return False
        head = self.api.get(f"repos/{REPOSITORY}/git/ref/heads/main")
        if head.get("object", {}).get("sha") != self.publisher_sha:
            return False
        if effect == "tag" and action in {"intent", "dispatch"}:
            if self.policy_reader is None:
                return False
            try:
                policy_join(self.policy_reader(), self.selection)
            except Refused:
                return False
        return True


# ---------------------------------------------------------------------------
# Repository-parametrized release API and provider


def make_api_class(engine):
    base = engine.provider.GitHubAPI

    class CoordinationAPI(base):
        """The shared API with Coordination release assets (the shared helper fixes FS-GG/.github)."""

        def upload_asset(self, release_id: int, name: str, path: pathlib.Path, expected_sha256=None) -> dict:
            require(type(expected_sha256) is str and re.fullmatch("[0-9a-f]{64}", expected_sha256) is not None
                    and not path.is_symlink() and path.is_file(), "release asset bytes are not bound to their digest")
            with path.open("rb") as stream:
                raw = stream.read(MAX_PACKAGE + 1)
            require(0 < len(raw) <= MAX_PACKAGE and sha256_hex(raw) == expected_sha256, "release asset bytes differ from their bound digest")
            url = f"https://uploads.github.com/repos/{REPOSITORY}/releases/{release_id}/assets?" + urllib.parse.urlencode({"name": name})
            return json.loads(self._request(url, "POST", raw, "application/octet-stream"))

        def download_asset(self, asset_id: int) -> bytes:
            url = f"https://api.github.com/repos/{REPOSITORY}/releases/assets/{asset_id}"
            request = urllib.request.Request(url, headers={"Authorization": f"Bearer {self.token}", "Accept": "application/octet-stream"})
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoCredentialRedirect)
            try:
                with opener.open(request, timeout=60) as response:
                    raw = response.read(MAX_PACKAGE + 1)
            except urllib.error.HTTPError as error:
                raise engine.provider.Refused(f"release asset {asset_id} returned HTTP {error.code}") from error
            require(len(raw) <= MAX_PACKAGE, "release asset exceeds its bound")
            return raw

    return CoordinationAPI


class PairProvider:
    """Observations and one-shot sends for the thirteen effect identities."""

    def __init__(self, engine, api, selection, candidate, *, transport, github_token="", nuget_key="", generated=None,
                 asset_dir=None, runner=None, allowed=None, work=None):
        self.engine, self.api, self.selection, self.candidate = engine, api, selection, candidate
        self.transport, self.github_token, self.nuget_key = transport, github_token, nuget_key
        self.generated = generated or {}
        self.asset_dir = pathlib.Path(asset_dir) if asset_dir is not None else candidate.directory
        self.runner = runner
        self.allowed = frozenset(allowed) if allowed is not None else frozenset(EFFECT_IDS)
        self.version, self.tag, self.source = selection["version"], selection["tag"], selection["source"]
        self.cid = content_id(selection)
        self.marker = f"execution-pair-forward:{self.cid}"
        self.feed_facts = {}
        self.last_dispatch_diagnostic = None
        self._draft_row = None

    # -- helpers
    def component_absent(self) -> bool:
        """The component tag and release are both absent (genesis only)."""
        try:
            self.api.get(f"repos/{REPOSITORY}/git/ref/tags/{self.tag}")
        except self.engine.provider.NotFound:
            return self._release() is None
        return False

    def download_feed(self, feed, package):
        return fetch_feed(self.transport, self.github_token, self.version, feed, package)

    def _release(self):
        for page in range(1, 11):
            rows = self.api.get(f"repos/{REPOSITORY}/releases?per_page=100&page={page}")
            require(isinstance(rows, list), "release list differs")
            matches = [r for r in rows if r.get("tag_name") == self.tag]
            require(len(matches) <= 1, "duplicate releases share the forward tag")
            if matches:
                return matches[0]
            if len(rows) < 100:
                return None
        raise self.engine.provider.Refused("release list exceeds bounded lookup")

    def _asset(self, release, name):
        rows = self.api.get(f"repos/{REPOSITORY}/releases/{release['id']}/assets?per_page=100")
        require(isinstance(rows, list) and len(rows) < 100, "release asset list differs")
        matches = [r for r in rows if r.get("name") == name]
        require(len(matches) <= 1, f"duplicate release asset {name}")
        return self.api.download_asset(matches[0]["id"]) if matches else None

    def _asset_digest(self, identity):
        name = asset_name(identity, self.version)
        kind = identity.removeprefix("asset:")
        if kind in IDS:
            return name, self.candidate.packages[kind]
        if kind == "manifest":
            return name, sha256_hex(self.candidate.manifest_bytes)
        require(kind in self.generated, "generated release asset is not yet derived")
        return name, sha256_hex(self.generated[kind])

    def _assets_all_matched(self, release):
        for identity in EFFECT_IDS[6:12]:
            name, digest = self._asset_digest(identity)
            raw = self._asset(release, name)
            if raw is None or sha256_hex(raw) != digest:
                return False
        return True

    def ready(self, effects) -> None:
        """Prove every send this stage may originate is possible *before* any durable intent exists.

        A send that cannot happen after its intent is written is a permanent Unknown, so
        credentials, retained bytes and generated assets are checked up front.
        """
        for effect in effects:
            e = effect.identity
            if e not in self.allowed:
                continue
            kind, _, package = e.partition(":")
            if kind in ("github", "nuget"):
                require(bool(self.github_token if kind == "github" else self.nuget_key), f"{e}: publishing credential missing")
                require(self.runner is not None, f"{e}: package push runner unavailable")
                path = self.candidate.directory / f"{package}.{self.version}.nupkg"
                require(path.is_file() and not path.is_symlink() and sha256_hex(path.read_bytes()) == self.candidate.packages[package], f"{e}: retained package bytes differ")
            elif kind == "asset":
                name, digest = self._asset_digest(e)
                path = (self.asset_dir / name) if package in GENERATED_ASSETS else (self.candidate.directory / name)
                require(path.is_file() and not path.is_symlink() and sha256_hex(path.read_bytes()) == digest, f"{e}: release asset bytes are not staged")
            elif e in ("tag", "draft", "promote"):
                require(self.api is not None, f"{e}: release API unavailable")

    # -- observation
    def observe(self, effect):
        e, O = effect.identity, self.engine.Observation
        kind, _, package = e.partition(":")
        if kind in ("github", "nuget"):
            raw = self.download_feed(kind, package)
            if raw is None:
                return O("absent")
            try:
                fact = feed_fact(kind, self.candidate.packages[package], raw, self.candidate.bytes[package])
            except Refused:
                return O("mismatched")
            self.feed_facts[e] = fact
            return O("matched", effect.target_digest)
        if e == "tag":
            try:
                ref = self.api.get(f"repos/{REPOSITORY}/git/ref/tags/{self.tag}")
            except self.engine.provider.NotFound:
                return O("absent")
            obj = ref.get("object", {})
            actual = obj.get("sha")
            return O("matched", actual) if (obj.get("type") == "commit" and actual == self.source) else O("mismatched", actual)
        if e == "draft":
            release = self._release()
            if release is None:
                return O("absent")
            # Promotion later flips draft to false, so the draft effect stays verifiable by tag, target and marker.
            valid = (release.get("tag_name") == self.tag and release.get("prerelease") is False
                     and release.get("target_commitish") == self.source and self.marker in (release.get("body") or ""))
            if valid:
                # Remembered for this transition: the engine observes every verified effect before any send.
                self._draft_row = release
            return O("matched", self.cid) if valid else O("mismatched")
        if kind == "asset":
            release = self._release()
            if release is None:
                return O("absent")
            name, digest = self._asset_digest(e)
            raw = self._asset(release, name)
            if raw is None:
                return O("absent")
            actual = sha256_hex(raw)
            return O("matched", actual) if actual == digest else O("mismatched", actual)
        if e == "promote":
            release = self._release()
            if release is None or release.get("draft") is not False:
                return O("absent")
            valid = (release.get("tag_name") == self.tag and self.marker in (release.get("body") or "") and self._assets_all_matched(release))
            return O("matched", self.cid) if valid else O("mismatched")
        raise Refused(f"unknown forward effect {e}")

    # -- sends (each executed at most once by the engine and fenced by SendFence)
    def dispatch(self, effect):
        e, D = effect.identity, self.engine.Dispatch
        kind, _, package = e.partition(":")
        self.last_dispatch_diagnostic = None
        try:
            require(e in self.allowed, "effect is outside this stage's credential window")
            if kind in ("github", "nuget"):
                key = self.github_token if kind == "github" else self.nuget_key
                require(bool(key), f"{kind} publishing credential missing")
                source = "https://nuget.pkg.github.com/FS-GG/index.json" if kind == "github" else "https://api.nuget.org/v3/index.json"
                path = self.candidate.directory / f"{package}.{self.version}.nupkg"
                require(sha256_hex(path.read_bytes()) == self.candidate.packages[package], "retained package bytes changed")
                require(self.runner is not None, "package push runner unavailable")
                self.runner(["dotnet", "nuget", "push", str(path), "--source", source, "--api-key", key])
            elif e == "tag":
                self.api.post(f"repos/{REPOSITORY}/git/refs", {"ref": f"refs/tags/{self.tag}", "sha": self.source})
            elif e == "draft":
                self.api.post(f"repos/{REPOSITORY}/releases", {"tag_name": self.tag, "target_commitish": self.source,
                              "name": f"Execution pair {self.version}", "body": self.marker, "draft": True, "prerelease": False})
            elif kind == "asset":
                # No remote read here: the draft was observed just before the intent and a failed read would strand the effect.
                release = self._draft_row
                require(release is not None and release.get("draft") is True, "forward draft release was not observed in this transition")
                name, digest = self._asset_digest(e)
                path = self.asset_dir / name if package in GENERATED_ASSETS else (self.candidate.directory / name)
                self.api.upload_asset(release["id"], name, path, digest)
            elif e == "promote":
                # Every asset effect was re-observed matched in this same transition, ahead of this send.
                release = self._draft_row
                require(release is not None and release.get("draft") is True, "forward draft release was not observed in this transition")
                self.api.patch(f"repos/{REPOSITORY}/releases/{release['id']}", {"draft": False, "make_latest": "false"})
            else:
                raise Refused(f"unknown forward effect {e}")
        except Exception as error:  # noqa: BLE001 - the send outcome is Unknown, never retried.
            category = "provider-refused" if isinstance(error, (Refused, self.engine.EngineRefused)) else type(error).__name__
            self.last_dispatch_diagnostic = {"category": category[:64]}
            return D("unknown")
        return D("applied")


# ---------------------------------------------------------------------------
# Stage runner


def sendable(stage: str) -> frozenset:
    """Effects this stage may originate: its window intersected with the credentials it holds."""
    return frozenset(e for e in STAGES[stage] if EFFECT_CREDENTIAL[e] in STAGE_CREDENTIALS[stage])


def prefix_verified(journal_state, stage: str) -> None:
    """The finalize stage may start only from a journal whose package window is fully verified."""
    if stage == "finalize":
        for identity in STAGES["packages"]:
            require(journal_state.effects.get(identity) == "verified", f"package window is not complete: {identity}")


def read_policy(api) -> bytes:
    """The complete tag-protection ruleset, read with a policy-only credential."""
    raw = canonical(api.get(f"repos/{REPOSITORY}/rulesets/{RULESET_ID}"))
    require(0 < len(raw) <= MAX_JSON, "ruleset evidence exceeds its bound")
    return raw


def run_stage(engine, stage, cid, effects, journal, admission, provider, *, sleep=None, clock=None, deadline_seconds=45 * 60, report=print):
    """Advance the shared journal inside one stage window; never resend, never exceed the window."""
    sleep, clock = sleep or time.sleep, clock or time.monotonic
    fence = SendFence()
    fenced_journal, fenced_provider = FencedJournal(journal, fence), FencedProvider(provider, fence)
    prefix_verified(journal.read(), stage)
    provider.ready(effects)
    deadline = clock() + deadline_seconds
    for _ in range(len(effects) * 2 + 120):
        result = engine.execution.advance_effects(cid, effects, fenced_journal, admission, fenced_provider)
        report(f"forward {stage} effect state: {result}")
        if result == "complete":
            return fence
        if clock() >= deadline:
            raise Refused("forward publication observation deadline expired")
        sleep(5)
    raise Refused("forward publication exceeded bounded reconciliation steps")


# ---------------------------------------------------------------------------
# Authenticated acquisition (read-only) and preflight


def _api_headers(token: str) -> dict:
    return {"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}


def _fetch(transport, url, token, path: pathlib.Path, cap: int, *, what: str, accept=None) -> bytes:
    headers = _api_headers(token)
    if accept:
        headers["Accept"] = accept
    status, raw = transport.get(url, headers=headers, cap=cap)
    require(status == 200 and 0 < len(raw) <= cap, f"{what} acquisition returned HTTP {status} or exceeded its bound")
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(raw)
    return raw


def acquire_candidate(transport, token: str, selection: dict, workdir) -> Candidate:
    """Authenticate the exact retained candidate archive through the maintained extractor."""
    workdir = pathlib.Path(workdir)
    root, out = workdir / "candidate-evidence", workdir / "candidate"
    c, api = selection["candidate"], f"https://api.github.com/repos/{REPOSITORY}/actions"
    _fetch(transport, f"{api}/runs/{c['runId']}", token, root / "run.json", 4194304, what="candidate run")
    _fetch(transport, f"{api}/runs/{c['runId']}/attempts/1/jobs?per_page=100", token, root / "jobs.json", 4194304, what="candidate jobs")
    _fetch(transport, f"{api}/artifacts/{c['artifactId']}", token, root / "artifact.json", 4194304, what="candidate artifact")
    _fetch(transport, f"{api}/artifacts/{c['artifactId']}/zip", token, root / "candidate.zip", 50331648, what="candidate archive")
    out.mkdir()
    admission_path = root / "pair-admission.json"
    admission_path.write_bytes(canonical(selection_as_pair_admission(selection)))
    guarded(harness_module()["extract_execution_pair_artifact"], admission_path, root, out, reason="candidate archive authentication differs")
    return Candidate(out, selection)


def acquire_receivers(transport, token: str, selection: dict, workdir) -> dict:
    """Authenticate the four distinct candidate receiver cases at the exact candidate source."""
    root = pathlib.Path(workdir) / "receivers"
    q, api = selection["consumerQualification"], f"https://api.github.com/repos/{REPOSITORY}/actions"
    _fetch(transport, f"{api}/runs/{q['runId']}", token, root / "run.json", MAX_JSON, what="receiver run")
    _fetch(transport, f"{api}/runs/{q['runId']}/attempts/1/jobs?per_page=100", token, root / "jobs.json", MAX_JSON, what="receiver jobs")
    for case, row in q["cases"].items():
        _fetch(transport, f"{api}/artifacts/{row['artifactId']}", token, root / f"{case}.artifact.json", MAX_JSON, what=f"{case} artifact")
        _fetch(transport, f"{api}/jobs/{row['jobId']}/logs", token, root / f"{case}.job.log", 8388608, what=f"{case} job log")
        _fetch(transport, f"{api}/artifacts/{row['artifactId']}/zip", token, root / f"{case}.zip", 33554432, what=f"{case} archive")
    proof = guarded(readback_module()["pair_consumers"], selection_as_pair_admission(selection), root, selection["receiverSources"],
                    reason="four receiver proofs differ")
    require(sorted(proof["cases"]) == sorted(q["cases"]) and len({r["runnerId"] for r in proof["cases"].values()}) == 4,
            "receiver cases are not four distinct hosted observations")
    return proof


def resume_plan(state) -> str:
    """Which stage a continuing invocation must run, from the shared journal alone."""
    effects = state.effects
    require(set(effects) <= set(EFFECT_IDS), "the journal holds an effect outside the forward route")
    verified = {e for e, v in effects.items() if v == "verified"}
    if verified == set(EFFECT_IDS):
        return "complete"
    if set(STAGES["packages"]) <= verified:
        return "finalize"
    require(not (set(effects) & set(EFFECT_IDS[4:])), "the journal advanced past the package window before it completed")
    return "packages"


def run_preflight(transport, selection: dict, *, token: str, policy_raw: bytes, workdir, resume: bool = False) -> dict:
    """Bounded authenticated read-only preflight; every join must hold before any credential exists.

    A resuming preflight (journal already begun) cannot require unoccupied cells: the engine
    re-observes every journal effect on each transition and refuses a contradiction.
    """
    candidate = acquire_candidate(transport, token, selection, workdir)
    receivers = acquire_receivers(transport, token, selection, workdir)
    observed = observe_occupancy(transport, selection, token)
    if resume:
        occupancy = {"version": selection["version"], "resume": True, "cells": dict(observed["cells"]), "tag": observed["tag"], "release": observed["release"]}
    else:
        occupancy = occupancy_join(selection, observed)
    policy = policy_join(policy_raw, selection)
    return {"schema": "fsgg.coordination.execution-pair-forward-preflight/1", "operation": selection["operation"], "contentId": content_id(selection),
            "version": selection["version"], "candidateManifestSha256": sha256_hex(candidate.manifest_bytes),
            "receiversSha256": sha256_hex(canonical(receivers)), "occupancy": occupancy, "policy": policy,
            "effects": list(EFFECT_IDS), "liveEffectsAuthorized": False}


def public_receipt_artifact_name(run_id) -> str:
    return f"execution-pair-forward-public-load-{run_id}-1"


def fetch_public_load_receipt(transport, token: str, selection: dict, run_id: int, workdir) -> bytes:
    """The receipt must come from this run's successful, earlier public-load job."""
    api = f"https://api.github.com/repos/{REPOSITORY}/actions"
    base = pathlib.Path(workdir) / "public-load-evidence"
    jobs = strict_json(_fetch(transport, f"{api}/runs/{run_id}/attempts/1/jobs?per_page=100", token, base / "jobs.json", MAX_JSON, what="publisher jobs"))
    rows = jobs.get("jobs") if isinstance(jobs, dict) else None
    require(isinstance(rows, list) and jobs.get("total_count") == len(rows) and len(rows) < 100, "publisher job page is incomplete")
    named = [j for j in rows if j.get("name") == "public-load"]
    require(len(named) == 1 and named[0].get("status") == "completed" and named[0].get("conclusion") == "success"
            and named[0].get("run_id") == run_id and named[0].get("run_attempt") == 1, "public package execution job did not succeed in this run")
    listing = strict_json(_fetch(transport, f"{api}/runs/{run_id}/artifacts?name={public_receipt_artifact_name(run_id)}", token, base / "artifacts.json", MAX_JSON, what="receipt listing"))
    artifacts = listing.get("artifacts") if isinstance(listing, dict) else None
    require(isinstance(artifacts, list) and len(artifacts) == 1, "public-load receipt artifact population differs")
    meta = artifacts[0]
    require(meta.get("name") == public_receipt_artifact_name(run_id) and meta.get("expired") is False and type(meta.get("id")) is int
            and (meta.get("workflow_run") or {}).get("id") == run_id and type(meta.get("digest")) is str, "public-load receipt artifact identity differs")
    archive = _fetch(transport, f"{api}/artifacts/{meta['id']}/zip", token, base / "receipt.zip", 4194304, what="receipt archive")
    require("sha256:" + sha256_hex(archive) == meta["digest"], "public-load receipt archive digest differs")
    with zipfile.ZipFile(io.BytesIO(archive)) as z:
        require(z.namelist() == ["public-load-receipt.json"], "public-load receipt archive population differs")
        info = z.getinfo("public-load-receipt.json")
        require(0 < info.file_size <= MAX_JSON, "public-load receipt exceeds its bound")
        return z.read(info)


# ---------------------------------------------------------------------------
# Read-only public package execution


def run_public_load(selection: dict, candidate: Candidate, *, transport, verify_signature, installed_check, workdir,
                    sleep=None, clock=None, window_seconds=900) -> bytes:
    """Execute the identical public bytes. No credentials, no mutation, bounded read polling only."""
    workdir = pathlib.Path(workdir)
    sleep, clock = sleep or time.sleep, clock or time.monotonic
    served, signatures = {}, {}
    deadline = clock() + window_seconds
    for package in IDS:
        while True:
            status, raw = transport.get(feed_url("nuget", package, selection["version"]), cap=MAX_PACKAGE)
            if status == 200:
                break
            require(status == 404, f"public package observation returned HTTP {status}")
            require(clock() < deadline, f"public package {package} did not become visible within its window")
            sleep(15)
        feed_fact("nuget", candidate.packages[package], raw, candidate.bytes[package])
        target = workdir / f"public-{package}.nupkg"
        target.write_bytes(raw)
        with zipfile.ZipFile(io.BytesIO(raw)) as z:
            signed = ".signature.p7s" in z.namelist()
        if signed:
            verify_signature(target)
        signatures[package] = "verified" if signed else "absent"
        served[package] = raw
    installed_path = workdir / "installed-load.json"
    installed_check(candidate.directory, "public", installed_path)
    installed = strict_json(installed_path.read_bytes())
    receipt = build_public_load_receipt(selection, candidate, served, signatures, installed)
    validate_public_load(receipt, selection, candidate)
    return receipt


# ---------------------------------------------------------------------------
# Finalize inputs


def derive_finalize_inputs(transport, token: str, selection: dict, candidate: Candidate, run_id: int, workdir) -> dict:
    """Authenticate everything the generated release assets are derived from."""
    receivers = acquire_receivers(transport, token, selection, workdir)
    receipt = fetch_public_load_receipt(transport, token, selection, run_id, workdir)
    validate_public_load(receipt, selection, candidate)
    feeds = {}
    for package in IDS:
        for feed in ("github", "nuget"):
            raw = fetch_feed(transport, token, selection["version"], feed, package)
            require(raw is not None, f"{feed} package {package} is not visible")
            feeds[f"{feed}:{package}"] = feed_fact(feed, candidate.packages[package], raw, candidate.bytes[package])
    generated = build_assets(selection, candidate, receivers=receivers, public_load=receipt, feeds=feeds)
    assets = pathlib.Path(workdir) / "assets"
    assets.mkdir()
    for kind, raw in generated.items():
        (assets / ASSET_NAMES[kind]).write_bytes(raw)
    return generated
