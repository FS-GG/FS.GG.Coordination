"""Pure, fake-port selection join for the closed v5 no-grant ZIP candidate.

This checks consistency of supplied observations. It neither authenticates the
observers nor authorizes installation or native dispatch.
"""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import re
from dataclasses import dataclass

import verify_callable_isolated_v2_v5_no_grant as byte_check

REPOSITORY = "FS-GG/FS.GG.Coordination"
SOURCE_SCHEMA = "fsgg.coordination.v5-no-grant-source-observation/1"
REVIEW_SCHEMA = "fsgg.coordination.v5-no-grant-review-observation/1"
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
CHOSEN = frozenset({"repositoryId", "revision", "sourceTree", "runId",
                    "runAttempt", "producerActorId", "artifactId",
                    "sourceRecordId", "reviewerActorId", "reviewEventId"})
BLOBS = frozenset({"entry", "builder", "verifier", "workflow", "manifest",
                   "archive"})
SCOPE = frozenset({"principalId", "credentialId", "repository",
                   "repositoryId", "permissions", "expiresAt"})
COMMON = frozenset({"repository", "repositoryId", "revision", "sourceTree",
                    "runId", "runAttempt", "producerActorId", "artifactId",
                    "manifestSha256", "archiveSha256", "workflowSha256",
                    "builderSha256", "verifierSha256", "entrySha256"})
SOURCE = COMMON | {"schema", "complete", "principalId", "credentialId",
                   "recordId", "observedAt"}
REVIEW = COMMON | {"schema", "complete", "principalId", "credentialId",
                   "eventId", "sourceRecordId", "reviewerActorId",
                   "decision", "reviewedAt", "expiresAt"}
DIGESTS = {"entry": "entrySha256", "builder": "builderSha256",
           "verifier": "verifierSha256", "workflow": "workflowSha256",
           "manifest": "manifestSha256", "archive": "archiveSha256"}
PINNED_BYTES = {
    "entry": "d235e8e7b7ef3a3056fdd03443fc4b7736b78d7844428c66aa83e434bfe42dc8",
    "builder": "cd44452c166496115b862b634e45bb0480ad117b0f584e43f42350c373f2655f",
    "verifier": "166f8a384fe1eab1842186436dafebb8a118deb3e6a2055a65c5f45408985726",
    "workflow": "7406efeabb5aca0504e8ae39a514038d7473b6fd97a66d2e4efcc5b2c94799db",
    "manifest": "f1cd7456e66bede7891e6295eb0437916d66d1f450dcdb12b0af141ddffa5e30",
    "archive": "a8b3bdc58f220061bb2e2e115d6aa0d042facd9881323a77dfe884b3b461f05c",
}


class Refused(ValueError):
    """Fixed refusal; never includes observer records or credential material."""


@dataclass(frozen=True)
class Selection:
    archive_sha256: str
    manifest_sha256: str
    revision: str
    source_tree: str
    producer_run_id: int
    producer_run_attempt: int
    review_event_id: int
    repository_id: int
    artifact_id: int
    producer_actor_id: int
    reviewer_actor_id: int
    source_record_id: int
    reviewed_at: str
    expires_at: str
    source_reader_principal: str
    source_credential_id: str
    review_reader_principal: str
    review_credential_id: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _positive(value):
    return type(value) is int and value > 0


def _hex(value, pattern):
    return type(value) is str and pattern.fullmatch(value) is not None \
        and value != "0" * len(value)


def _time(value):
    if type(value) is not str or not re.fullmatch(
            r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", value):
        raise Refused("v5-selection-time")
    try:
        return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise Refused("v5-selection-time") from None


def _shape(value, keys):
    if type(value) is not dict or set(value) != keys:
        raise Refused("v5-selection-shape")


def _scope(value, role, chosen, now):
    _shape(value, SCOPE)
    permissions = (["actions:read", "contents:read"] if role == "source"
                   else ["read-release-approval"])
    if (type(value["principalId"]) is not str
            or not value["principalId"]
            or not _hex(value["credentialId"], HEX64)
            or value["repository"] != REPOSITORY
            or type(value["repositoryId"]) is not int
            or value["repositoryId"] != chosen["repositoryId"]
            or type(value["permissions"]) is not list
            or value["permissions"] != permissions
            or not now < _time(value["expiresAt"]) <= now + dt.timedelta(minutes=30)):
        raise Refused("v5-selection-scope")


def qualify(source_port, review_port, chosen, blobs, now):
    """Join exact candidate bytes to separate injected observations; stay closed."""
    if (source_port is None or review_port is None
            or source_port is review_port
            or type(now) is not dt.datetime or now.tzinfo is None
            or now.utcoffset() != dt.timedelta(0)):
        raise Refused("v5-selection-input")
    try:
        selected = copy.deepcopy(chosen)
        supplied = copy.deepcopy(blobs)
        _shape(selected, CHOSEN)
        _shape(supplied, BLOBS)
        if (not all(_positive(selected[k]) for k in CHOSEN
                    if k not in {"revision", "sourceTree"})
                or not _hex(selected["revision"], HEX40)
                or not _hex(selected["sourceTree"], HEX40)
                or selected["producerActorId"] == selected["reviewerActorId"]
                or not all(type(raw) is bytes and 0 < len(raw) <= 256_000
                           for raw in supplied.values())):
            raise Refused("v5-selection-input")
        if any(hashlib.sha256(supplied[blob]).hexdigest() != digest
               for blob, digest in PINNED_BYTES.items()):
            raise Refused("v5-selection-pin")
        source_scope = copy.deepcopy(source_port.scope())
        review_scope = copy.deepcopy(review_port.scope())
        _scope(source_scope, "source", selected, now)
        _scope(review_scope, "review", selected, now)
        if (source_scope["principalId"] == review_scope["principalId"]
                or source_scope["credentialId"] == review_scope["credentialId"]):
            raise Refused("v5-selection-shared-reader")
        source = copy.deepcopy(source_port.read(selected["sourceRecordId"]))
        review = copy.deepcopy(review_port.read(selected["reviewEventId"]))
        _shape(source, SOURCE)
        _shape(review, REVIEW)
        if (source["schema"] != SOURCE_SCHEMA or review["schema"] != REVIEW_SCHEMA
                or source["complete"] is not True or review["complete"] is not True
                or source["principalId"] != source_scope["principalId"]
                or source["credentialId"] != source_scope["credentialId"]
                or review["principalId"] != review_scope["principalId"]
                or review["credentialId"] != review_scope["credentialId"]
                or source["recordId"] != selected["sourceRecordId"]
                or review["eventId"] != selected["reviewEventId"]
                or review["sourceRecordId"] != selected["sourceRecordId"]
                or review["reviewerActorId"] != selected["reviewerActorId"]
                or review["decision"] != "inspect-only-release"):
            raise Refused("v5-selection-record")
        if (type(source["recordId"]) is not int
                or type(review["eventId"]) is not int
                or type(review["sourceRecordId"]) is not int
                or type(review["reviewerActorId"]) is not int):
            raise Refused("v5-selection-record-type")
        for record in (source, review):
            if (record["repository"] != REPOSITORY
                    or type(record["repository"]) is not str
                    or any(type(record[k]) is not int for k in
                           ("repositoryId", "runId", "runAttempt",
                            "producerActorId", "artifactId"))
                    or any(type(record[k]) is not str for k in
                           ("revision", "sourceTree"))
                    or any(record[k] != selected[k] for k in
                           ("repositoryId", "revision", "sourceTree", "runId",
                            "runAttempt", "producerActorId", "artifactId"))):
                raise Refused("v5-selection-coordinate")
            for blob, field in DIGESTS.items():
                if (not _hex(record[field], HEX64)
                        or record[field] != hashlib.sha256(supplied[blob]).hexdigest()):
                    raise Refused("v5-selection-digest")
        observed = _time(source["observedAt"])
        reviewed = _time(review["reviewedAt"])
        expires = _time(review["expiresAt"])
        if not (now - dt.timedelta(minutes=30) <= observed <= reviewed <= now
                and now - dt.timedelta(minutes=30) <= reviewed
                and now < expires <= now + dt.timedelta(minutes=30)):
            raise Refused("v5-selection-stale")
        checked = byte_check.verify(supplied["archive"], supplied["manifest"],
            review["manifestSha256"], supplied["entry"], supplied["builder"],
            supplied["workflow"])
        if checked != {"schema":
                "fsgg.coordination.callable-isolated-v2-v5-no-grant-byte-check/1",
                "verified": True, "authorized": False, "canDispatch": False}:
            raise Refused("v5-selection-byte-check")
        if (source_port.scope() != source_scope
                or review_port.scope() != review_scope
                or chosen != selected or blobs != supplied):
            raise Refused("v5-selection-drift")
        return Selection(review["archiveSha256"], review["manifestSha256"],
                         selected["revision"], selected["sourceTree"],
                         selected["runId"], selected["runAttempt"],
                         selected["reviewEventId"],
                         selected["repositoryId"], selected["artifactId"],
                         selected["producerActorId"], selected["reviewerActorId"],
                         selected["sourceRecordId"], review["reviewedAt"],
                         review["expiresAt"], source_scope["principalId"],
                         source_scope["credentialId"],
                         review_scope["principalId"],
                         review_scope["credentialId"])
    except Refused:
        raise
    except Exception:
        raise Refused("v5-selection-unavailable") from None
