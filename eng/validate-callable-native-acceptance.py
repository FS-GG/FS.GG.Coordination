#!/usr/bin/env python3
"""Check the retained V2-CALL-01.4c acceptance against downloaded Actions artifacts.

Download the three archives by the IDs in native-acceptance.json before running.
This is an offline check and makes no provider request or mutation.
"""

import argparse
import base64
import hashlib
import json
import pathlib
import sys
import zipfile


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()


def sha256(value):
    return hashlib.sha256(value).hexdigest()


def require(condition, label):
    if not condition:
        raise ValueError(label)


def artifact(path, expected_hash, member):
    archive = path.read_bytes()
    require(sha256(archive) == expected_hash, f"{member}: archive digest")
    with zipfile.ZipFile(path) as zipped:
        require(zipped.namelist() == [member], f"{member}: member set")
        return zipped.read(member)


def sealed(value, field):
    require(isinstance(value, dict) and isinstance(value.get(field), str), f"{field}: missing")
    body = {key: item for key, item in value.items() if key != field}
    require(sha256(canonical(body)) == value[field], f"{field}: invalid")


def validate(evidence, prior_archive, checkpoint_archive, receipt_archive):
    require(evidence.get("schema") == "fsgg.coordination.callable-native-acceptance/1", "evidence schema")
    prior_ref = evidence["retainedInterruption"]
    artifacts = evidence["settledArtifacts"]
    expected = evidence["acceptedIdentity"]
    prior = json.loads(artifact(prior_archive, prior_ref["checkpointArchiveSha256"],
                                "callable-isolated-operation-checkpoint.json"))
    checkpoint_bytes = artifact(checkpoint_archive, artifacts["checkpointArchiveSha256"],
                                "callable-isolated-operation-checkpoint.json")
    receipt_bytes = artifact(receipt_archive, artifacts["receiptArchiveSha256"],
                             "callable-isolated-operation-receipt.json")
    require(checkpoint_bytes == receipt_bytes, "settled checkpoint and receipt differ")
    receipt = json.loads(receipt_bytes)
    sealed(prior, "seal")
    sealed(receipt, "receiptSha256")
    require(prior["schema"] == "fsgg.coordination.callable-isolated-operation-progress/1"
            and prior["stage"] == prior_ref["stage"] == "cli-intent", "retained interruption stage")
    require(receipt["schema"] == "fsgg.coordination.callable-isolated-operation-receipt/1", "receipt schema")
    require(receipt["receiptSha256"] == artifacts["receiptSeal"], "receipt seal identity")

    source = receipt["source"]
    installed = receipt["installed"]
    package = installed["package"]
    native = receipt["nativeReadback"]
    cleanup = receipt["cleanup"]
    target = receipt["target"]
    plan_bytes = base64.b64decode(installed["planBytesBase64"], validate=True)
    plan = json.loads(plan_bytes)
    require(plan_bytes == base64.b64decode(prior["installedPlanBase64"], validate=True),
            "retained native plan changed")
    require(sha256(plan_bytes) == installed["planSha256"] == prior["installedPlanSha256"],
            "native plan digest")

    observed = {
        "operationIdentity": receipt["operationIdentity"],
        "contractSha256": receipt["contractSha256"],
        "target": target["fullName"],
        "repositoryId": target["repositoryId"],
        "sourceBaseSha": source["baseSha"],
        "sourceHeadSha": source["headSha"],
        "pullRequestNumber": source["pullRequestNumber"],
        "pullRequestNodeId": source["pullRequestNodeId"],
        "mergeCommit": native["mergeCommit"],
        "installedPackageVersion": package["version"],
        "packageCandidateSha256": package["candidateSha256"],
        "nugetOrgServedSha256": package["nugetOrgServedSha256"],
        "installedManagedCommandSha256": package["installedManagedCommandSha256"],
        "installedPlanSha256": installed["planSha256"],
        "installedPlanSeal": installed["planSeal"],
        "nativeOperationId": native["journalOperationId"],
        "journalRef": source["journalRef"],
        "journalGeneration": native["journalGeneration"],
        "journalSha256": native["journalSha256"],
        "firstOutcome": installed["firstOutcome"],
        "freshProcessOutcome": installed["freshProcessOutcome"],
        "cleanupState": cleanup["state"],
        "cleanupReadbackHttpStatus": cleanup["repositoryHttpStatus"],
    }
    require(observed == expected, "accepted identity does not match sealed receipt")
    require(prior["operationIdentity"] == receipt["operationIdentity"]
            and prior["target"]["repositoryId"] == target["repositoryId"]
            and prior["target"]["fullName"] == target["fullName"]
            and prior["baseSha"] == source["baseSha"] == plan["baseSha"]
            and prior["sourceSha"] == source["headSha"] == plan["headSha"]
            and prior["pullRequest"]["number"] == source["pullRequestNumber"] == plan["pullRequestNumber"]
            and prior["pullRequest"]["nodeId"] == source["pullRequestNodeId"] == plan["pullRequestNodeId"]
            and prior["journalRef"] == source["journalRef"], "retained operation continuity")
    require(plan["schema"] == "fsgg.coordination.ordinary-delivery-plan/2"
            and plan["operationId"] == native["journalOperationId"]
            and plan["seal"] == installed["planSeal"] == prior["installedPlanSeal"]
            == native["journalPlanDigest"], "native plan and journal binding")
    require(native["journalStage"] == "settled" and native["journalGeneration"] >= 1
            and installed["firstOutcome"] == "native-readback"
            and installed["freshProcessOutcome"] == "AdvanceAlreadySettled"
            and cleanup["state"] == "settled"
            and cleanup["expectedRepositoryId"] == target["repositoryId"]
            and cleanup["repositoryHttpStatus"] == 404, "native acceptance predicates")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=pathlib.Path)
    parser.add_argument("prior_checkpoint_zip", type=pathlib.Path)
    parser.add_argument("settled_checkpoint_zip", type=pathlib.Path)
    parser.add_argument("settled_receipt_zip", type=pathlib.Path)
    args = parser.parse_args()
    try:
        validate(json.loads(args.evidence.read_bytes()), args.prior_checkpoint_zip,
                 args.settled_checkpoint_zip, args.settled_receipt_zip)
    except (KeyError, TypeError, ValueError, zipfile.BadZipFile) as error:
        print(f"native-acceptance-refused: {error}", file=sys.stderr)
        return 1
    print("native-acceptance: exact retained operation settled")
    return 0


if __name__ == "__main__":
    sys.exit(main())
