#!/usr/bin/env python3
"""Validate the V2-CALL-01.5b discovery handoff and optional public identities."""

import argparse
import base64
import hashlib
import json
import pathlib
import subprocess
import sys
import urllib.request


ROOT = pathlib.Path(__file__).resolve().parents[1]
HANDOFF = ROOT / "evidence/github-substrate-v2/gs2-09-9/callable-discovery-handoff.json"
PACKET = ROOT / "evidence/github-substrate-v2/gs2-09-9/callable-readiness.json"


def require(condition, label):
    if not condition:
        raise ValueError(label)


def sha256(value):
    return hashlib.sha256(value).hexdigest()


def git(*args):
    return subprocess.run(
        ["git", *args], cwd=ROOT, check=True, stdout=subprocess.PIPE
    ).stdout


def get(url):
    request = urllib.request.Request(
        url,
        headers={"Accept": "application/vnd.github+json", "User-Agent": "fsgg-callable-handoff/1"},
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read())


def remote_bytes(repository, path, revision):
    value = get(f"https://api.github.com/repos/{repository}/contents/{path}?ref={revision}")
    return value, base64.b64decode("".join(value["content"].split()), validate=True)


def validate_tracked(handoff, packet):
    require(handoff["schema"] == "fsgg.coordination.callable-discovery-handoff/1", "handoff schema")
    require(handoff["item"] == "V2-CALL-01.5b", "handoff item")
    require(handoff["originalItem"] == "V2-CALL-01.5", "original item")

    identity = handoff["packet"]
    require(identity["item"] == packet["item"] == "V2-CALL-01.5a", "packet item")
    require(sha256(PACKET.read_bytes()) == identity["sha256"], "current packet digest")
    require(git("rev-parse", f"{identity['protectedMerge']}^{{tree}}").decode().strip() == identity["protectedMergeTree"], "packet merge tree")
    protected_packet = git("show", f"{identity['protectedMerge']}:{identity['path']}")
    protected_validator = git("show", f"{identity['protectedMerge']}:{identity['validator']['path']}")
    require(sha256(protected_packet) == identity["sha256"], "protected packet digest")
    require(sha256(protected_validator) == identity["validator"]["sha256"], "protected validator digest")
    require(git("rev-parse", f"{identity['protectedMerge']}:{identity['path']}").decode().strip() == identity["blob"], "protected packet blob")
    require(git("rev-parse", f"{identity['protectedMerge']}:{identity['validator']['path']}").decode().strip() == identity["validator"]["blob"], "protected validator blob")

    expected_runtime = {
        "packageId": packet["release"]["packageId"],
        "version": packet["release"]["version"],
        "command": packet["receiver"]["command"],
        **packet["installedRuntime"],
    }
    require(handoff["runtimeInterface"] == expected_runtime, "runtime interface projection")
    require(handoff["recoveryPreconditions"] == packet["preconditions"], "recovery preconditions")
    require(handoff["recovery"] == packet["recovery"], "recovery behavior")
    require(handoff["permissionCeiling"] == packet["permissionCeiling"], "permission ceiling")
    native = handoff["historicalNativeAcceptance"]
    require(native["labels"] == packet["nativeAcceptance"]["historicalLabels"] == ["V2-CALL-01.4b", "V2-CALL-01.4c"], "historical labels")
    require(native["operationIdentity"] == packet["nativeAcceptance"]["operationIdentity"], "historical operation")
    require(native["contractSha256"] == packet["nativeAcceptance"]["contractSha256"], "historical contract")

    expected_gates = [
        ("discovery", "GS2-09.1"),
        ("manifest", "GS2-09.2"),
        ("transform", "GS2-09.3"),
        ("archive", "GS2-09.5"),
        ("rollback", "GS2-09.6"),
        ("representative-rehearsal", "GS2-09.7"),
        ("omission", "GS2-09.8"),
    ]
    actual_gates = [(gate["name"], gate["unit"]) for gate in handoff["pendingGates"]]
    require(actual_gates == expected_gates, "pending gate identities")
    require(all(gate["status"] == "pending" and gate["independent"] is True for gate in handoff["pendingGates"]), "pending gate state")
    require(handoff["completionClaim"] == "Receipt of the protected callable-readiness packet by GS2-09 discovery only.", "completion boundary")
    require(handoff["notAccepted"] == [
        "GS2-09 discovery or migration",
        "migration manifest, transform, live-operation handling, archive, rollback, omission or representative rehearsal",
        "OpenV2",
        "Q4",
        "production default",
    ], "non-claim boundary")

    roadmap = (ROOT / "docs/roadmaps/callable-ordinary-v2-execution.md").read_text()
    require("- [x] **V2-CALL-01.5 — Hand off exact callable readiness to GS2-09 migration.**" in roadmap, "parent completion")
    require("  - [x] **V2-CALL-01.5b — Hand the packet to GS2-09 discovery.**" in roadmap, "member completion")


def validate_live(handoff):
    identity = handoff["packet"]
    pull = get(f"https://api.github.com/repos/{identity['repository']}/pulls/{identity['pullRequest']}")
    require(pull["head"]["sha"] == identity["candidateHead"], "live packet candidate")
    require(pull["merge_commit_sha"] == identity["protectedMerge"] and pull["merged_at"], "live packet merge")
    commit = get(f"https://api.github.com/repos/{identity['repository']}/commits/{identity['protectedMerge']}")
    require(commit["commit"]["tree"]["sha"] == identity["protectedMergeTree"], "live packet tree")
    packet_meta, packet_bytes = remote_bytes(identity["repository"], identity["path"], identity["protectedMerge"])
    require(packet_meta["sha"] == identity["blob"] and sha256(packet_bytes) == identity["sha256"], "live packet bytes")
    validator_meta, validator_bytes = remote_bytes(identity["repository"], identity["validator"]["path"], identity["protectedMerge"])
    require(validator_meta["sha"] == identity["validator"]["blob"] and sha256(validator_bytes) == identity["validator"]["sha256"], "live validator bytes")

    consumer = handoff["consumer"]
    mapping_meta, mapping_bytes = remote_bytes(consumer["roadmapRepository"], consumer["populationMappingPath"], consumer["roadmapRevision"])
    require(mapping_meta["sha"] == consumer["populationMappingBlob"] and sha256(mapping_bytes) == consumer["populationMappingSha256"], "live population mapping")
    mapping = json.loads(mapping_bytes)
    public_rows = {(row["itemId"], row["originalItemId"]) for row in mapping["assignments"]}
    require(public_rows == {("V2-CALL-01.5a", "V2-CALL-01.5"), ("V2-CALL-01.5b", "V2-CALL-01.5")}, "public population rows")
    roadmap_meta, roadmap_bytes = remote_bytes(consumer["roadmapRepository"], consumer["roadmapPath"], consumer["roadmapRevision"])
    require(roadmap_meta["sha"] == consumer["roadmapBlob"] and sha256(roadmap_bytes) == consumer["roadmapSha256"], "live GS2 roadmap")
    roadmap = roadmap_bytes.decode()
    for gate in handoff["pendingGates"]:
        require(f'- [ ] **{gate["unit"]} —' in roadmap, f'live pending gate: {gate["unit"]}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", help="also verify public GitHub identities")
    args = parser.parse_args()
    try:
        handoff = json.loads(HANDOFF.read_bytes())
        packet = json.loads(PACKET.read_bytes())
        validate_tracked(handoff, packet)
        if args.live:
            validate_live(handoff)
    except (KeyError, TypeError, ValueError, json.JSONDecodeError, OSError, subprocess.CalledProcessError) as error:
        print(f"callable-discovery-handoff-refused: {error}", file=sys.stderr)
        return 1
    print(f"callable-discovery-handoff: tracked bindings valid; live={'valid' if args.live else 'not-requested'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
