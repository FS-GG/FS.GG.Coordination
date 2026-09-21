#!/usr/bin/env python3
"""Validate V2-CALL-01.5a tracked bindings and optional public live identities."""

import argparse
import base64
import hashlib
import json
import pathlib
import re
import sys
import urllib.request


ROOT = pathlib.Path(__file__).resolve().parents[1]
PACKET = ROOT / "evidence/github-substrate-v2/gs2-09-9/callable-readiness.json"


def require(condition, label):
    if not condition:
        raise ValueError(label)


def sha256(value):
    return hashlib.sha256(value).hexdigest()


def get(url, json_response=False):
    request = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": "fsgg-callable-readiness/1"})
    with urllib.request.urlopen(request, timeout=60) as response:
        value = response.read()
    return json.loads(value) if json_response else value


def validate_tracked(packet):
    require(packet["schema"] == "fsgg.coordination.callable-readiness/1", "packet schema")
    require(packet["item"] == "V2-CALL-01.5a", "item identity")
    for relative, expected in {**packet["source"]["runtimeFiles"], **packet["trackedEvidence"]}.items():
        path = ROOT / relative
        require(path.is_file() and sha256(path.read_bytes()) == expected, f"tracked digest: {relative}")

    contract = json.loads((ROOT / "eng/callable-cli-isolated-operation-contract.json").read_bytes())
    native = json.loads((ROOT / "evidence/github-substrate-v2/gs2-09-9/native-acceptance.json").read_bytes())
    recovery = json.loads((ROOT / "evidence/github-substrate-v2/gs2-09-9/recovery-coverage.json").read_bytes())
    require(contract["identity"] == packet["nativeAcceptance"]["operationIdentity"], "operation identity")
    require(contract["contractSha256"] == packet["nativeAcceptance"]["contractSha256"], "contract identity")
    require(contract["package"]["version"] == packet["release"]["version"], "installed version")
    require(contract["package"]["candidateSha256"] == packet["release"]["candidateSha256"], "candidate package")
    require(contract["package"]["nugetOrgServedSha256"] == packet["release"]["nugetOrgServedSha256"], "NuGet package")
    require(contract["package"]["installedManagedCommandSha256"] == packet["installedRuntime"]["managedCommandSha256"], "installed command")
    require(contract["receiver"]["merge"] == packet["receiver"]["adoptingRevision"], "receiver revision")
    require(contract["receiver"]["manifestSha256"] == packet["receiver"]["manifestSha256"], "receiver manifest")
    require(contract["authorization"]["credentialRoles"]["execution"]["permissions"] == packet["permissionCeiling"]["installedExecution"], "execution permission ceiling")
    roles = contract["authorization"]["credentialRoles"]
    observed_roles = {
        "authorityObserver": [f"{key}:{value}" for key, value in roles["authority-observer"]["permissions"].items()],
        "reviewerMembershipObserver": [f"{key}:{value}" for key, value in roles["reviewer-membership-observer"]["permissions"].items()],
        **{name: [f"{key}:{value}" for key, value in roles[name]["permissions"].items()] for name in ("creation", "setup", "execution", "cleanup")},
    }
    require(observed_roles == packet["permissionCeiling"]["operationRoles"], "operation permission ceiling")
    require(set(contract["forbidden"]) == set(packet["permissionCeiling"]["forbidden"]), "forbidden effects")
    operator = (ROOT / packet["installedRuntime"]["operatorInterpreter"]).read_bytes()
    require(sha256(operator) == packet["installedRuntime"]["operatorInterpreterSha256"], "operator interpreter")
    schema_sources = operator + (ROOT / "eng/callable-cli-isolated-operation-contract.json").read_bytes() + (ROOT / "src/FS.GG.Coordination.GitHub/OrdinaryDelivery.fs").read_bytes()
    schemas = sorted(set(re.findall(rb"fsgg\.coordination\.[A-Za-z0-9._/-]+", schema_sources)))
    require([value.decode() for value in schemas] == packet["installedRuntime"]["schemas"], "schema identities")
    delivery_source = (ROOT / "src/FS.GG.Coordination.Cli/DeliveryCommand.fs").read_text()
    require(all(operation.split()[-1] in delivery_source for operation in packet["installedRuntime"]["operations"]), "installed operations")
    require(native["protectedRun"] == packet["nativeAcceptance"]["protectedRun"], "protected native run")
    for field in ("operationIdentity", "contractSha256", "mergeCommit", "installedPlanSha256", "journalGeneration", "journalSha256", "freshProcessOutcome", "cleanupReadbackHttpStatus"):
        require(native["acceptedIdentity"][field] == packet["nativeAcceptance"][field], f"native acceptance: {field}")
    require(recovery["permissionCeiling"] == {"externalProviderMutation": False, "liveAcceptance": False, "openV2": False, "q4": False}, "historical .4a ceiling")
    require(packet["knownLimitations"][-1] == "This packet does not accept migration, OpenV2, Q4 or a production default.", "non-claim boundary")


def validate_live(packet):
    source = packet["source"]
    commit = get(f"https://api.github.com/repos/{source['repository']}/commits/{source['protectedMerge']}", True)
    require(commit["sha"] == source["protectedMerge"] and commit["commit"]["tree"]["sha"] == source["protectedMergeTree"], "live protected source")
    release_source = get(f"https://api.github.com/repos/{source['repository']}/commits/{source['releaseSourceCommit']}", True)
    require(release_source["sha"] == source["releaseSourceCommit"] and release_source["commit"]["tree"]["sha"] == source["releaseSourceTree"], "live release source")
    for path, expected in source["runtimeFiles"].items():
        content = get(f"https://api.github.com/repos/{source['repository']}/contents/{path}?ref={source['protectedMerge']}", True)
        value = base64.b64decode("".join(content["content"].split()), validate=True)
        require(sha256(value) == expected, f"live runtime source: {path}")
    tag = get(f"https://api.github.com/repos/{source['repository']}/git/ref/tags/{source['tag']}", True)
    require(tag["object"]["sha"] == source["protectedMerge"], "live tag")
    release = get(f"https://api.github.com/repos/{source['repository']}/releases/tags/{source['tag']}", True)
    asset = next(item for item in release["assets"] if item["name"] == f"{packet['release']['packageId']}.{packet['release']['version']}.nupkg")
    require(asset.get("digest") == "sha256:" + packet["release"]["githubReleaseAssetSha256"], "live release asset digest")
    release_bytes = get(asset["browser_download_url"])
    require(sha256(release_bytes) == packet["release"]["githubReleaseAssetSha256"], "live release asset bytes")
    package_name = packet["release"]["packageId"].lower()
    version = packet["release"]["version"]
    nuget = get(f"https://api.nuget.org/v3-flatcontainer/{package_name}/{version}/{package_name}.{version}.nupkg")
    require(sha256(nuget) == packet["release"]["nugetOrgServedSha256"], "live NuGet bytes")

    receiver = packet["receiver"]
    revision = get(f"https://api.github.com/repos/{receiver['repository']}/commits/{receiver['adoptingRevision']}", True)
    require(revision["commit"]["tree"]["sha"] == receiver["adoptingTree"], "live adopting receiver revision")
    execution = get(f"https://api.github.com/repos/{receiver['repository']}/commits/{receiver['qualifiedExecutionRevision']}", True)
    require(execution["commit"]["tree"]["sha"] == receiver["qualifiedExecutionTree"], "live execution receiver revision")
    manifest = get(f"https://api.github.com/repos/{receiver['repository']}/contents/{receiver['manifest']}?ref={receiver['adoptingRevision']}", True)
    manifest_bytes = base64.b64decode("".join(manifest["content"].split()), validate=True)
    require(manifest["sha"] == receiver["manifestBlob"] and sha256(manifest_bytes) == receiver["manifestSha256"], "live receiver manifest")
    manifest_json = json.loads(manifest_bytes)
    tool = manifest_json["tools"][receiver["packageKey"]]
    require(tool == {"version": version, "commands": [receiver["command"]], "rollForward": receiver["rollForward"]}, "live installed command")

    accepted = packet["nativeAcceptance"]["protectedRun"]
    run = get(f"https://api.github.com/repos/{accepted['repository']}/actions/runs/{accepted['runId']}", True)
    require(run["run_attempt"] == accepted["attempt"] and run["head_sha"] == accepted["headSha"] and run["conclusion"] == accepted["conclusion"], "live native run")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live", action="store_true", help="also verify public GitHub, release, receiver, run and NuGet identities")
    args = parser.parse_args()
    try:
        packet = json.loads(PACKET.read_bytes())
        validate_tracked(packet)
        if args.live:
            validate_live(packet)
    except (KeyError, StopIteration, TypeError, ValueError, json.JSONDecodeError, OSError) as error:
        print(f"callable-readiness-refused: {error}", file=sys.stderr)
        return 1
    print(f"callable-readiness: tracked bindings valid; live={'valid' if args.live else 'not-requested'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
