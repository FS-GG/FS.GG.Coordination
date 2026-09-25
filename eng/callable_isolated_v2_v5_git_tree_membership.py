"""Pure raw Git object membership for the closed v5 no-grant candidate."""

from __future__ import annotations

import copy
import dataclasses
import datetime as dt
import hashlib
from typing import Any

import build_callable_isolated_v2_v5_no_grant as builder
import callable_isolated_v2_effect_git_tree_witness as git
import callable_isolated_v2_v5_artifact_workflow_witness as producer
import callable_isolated_v2_v5_no_grant_selection as candidate
import verify_callable_isolated_v2_v5_no_grant as byte_check

IDENTITY_SCHEMA = git.IDENTITY_SCHEMA
BLOBS = {"entry", "builder", "verifier", "manifest", "workflow", "archive"}
PATHS = {builder.ENTRY_SOURCE: "entry",
         "eng/build_callable_isolated_v2_v5_no_grant.py": "builder",
         "eng/verify_callable_isolated_v2_v5_no_grant.py": "verifier",
         builder.MANIFEST: "manifest", builder.WORKFLOW: "workflow"}


class Refused(ValueError):
    """Fixed refusal without raw source or credential bytes."""


@dataclasses.dataclass(frozen=True)
class TreeWitness:
    revision: str
    source_tree: str
    source_files: tuple[tuple[str, str], ...]
    identity_event_id: int
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def qualify(selection: candidate.Selection, prior: producer.Witness,
            blobs: dict[str, bytes], git_port, identity_port,
            identity_event_id: int, now: dt.datetime) -> TreeWitness:
    """Compare injected raw objects; never fetch, install or authorize."""
    try:
        if (type(selection) is not candidate.Selection
                or type(prior) is not producer.Witness
                or type(blobs) is not dict or set(blobs) != BLOBS):
            raise Refused("v5-tree-input")
        original_selection = selection
        original_prior = prior
        original_blobs = blobs
        selection = copy.deepcopy(selection)
        prior = copy.deepcopy(prior)
        blobs = copy.deepcopy(blobs)
        if (selection.authorized is not False
                or selection.can_dispatch is not False
                or type(selection.live_effects) is not int
                or selection.live_effects != 0
                or prior.authorized is not False
                or prior.can_dispatch is not False
                or type(prior.live_effects) is not int
                or prior.live_effects != 0
                or prior.revision != selection.revision
                or prior.source_tree != selection.source_tree
                or prior.artifact_id != selection.artifact_id
                or prior.archive_sha256 != selection.archive_sha256
                or prior.workflow_sha256 != candidate.PINNED_BYTES["workflow"]
                or selection.archive_sha256 != candidate.PINNED_BYTES["archive"]
                or selection.manifest_sha256 != candidate.PINNED_BYTES["manifest"]
                or not candidate._hex(selection.revision, candidate.HEX40)
                or not candidate._hex(selection.source_tree, candidate.HEX40)
                or type(selection.repository_id) is not int
                or selection.repository_id <= 0
                or type(identity_event_id) is not int
                or identity_event_id <= 0
                or git_port is None or identity_port is None
                or git_port is identity_port
                or type(now) is not dt.datetime or now.tzinfo is None
                or now.utcoffset() != dt.timedelta(0)):
            raise Refused("v5-tree-selection")
        for name, expected in candidate.PINNED_BYTES.items():
            raw = blobs[name]
            if (type(raw) is not bytes or not 0 < len(raw) <= 256_000
                    or hashlib.sha256(raw).hexdigest() != expected):
                raise Refused("v5-tree-byte-pin")
        checked = byte_check.verify(blobs["archive"], blobs["manifest"],
            selection.manifest_sha256, blobs["entry"], blobs["builder"],
            blobs["workflow"])
        if checked != {"schema":
                "fsgg.coordination.callable-isolated-v2-v5-no-grant-byte-check/1",
                "verified": True, "authorized": False, "canDispatch": False}:
            raise Refused("v5-tree-byte-check")
        git_scope = git._scope(git_port, selection.repository_id,
                               ["contents:read", "metadata:read"], now)
        identity_scope = git._scope(identity_port, selection.repository_id,
                                   ["metadata:read"], now)
        used_principals = {selection.source_reader_principal,
                           selection.review_reader_principal,
                           prior.artifact_reader_principal,
                           prior.workflow_reader_principal}
        used_credentials = {selection.source_credential_id,
                            selection.review_credential_id,
                            prior.artifact_credential_id,
                            prior.workflow_credential_id}
        if (git_scope["principalId"] == identity_scope["principalId"]
                or git_scope["credentialId"] == identity_scope["credentialId"]
                or git_scope["principalId"] in used_principals
                or identity_scope["principalId"] in used_principals
                or git_scope["credentialId"] in used_credentials
                or identity_scope["credentialId"] in used_credentials):
            raise Refused("v5-tree-reader-custody")
        git._identity(identity_port, identity_event_id, identity_scope,
                      selection.repository_id, now)
        commit = git._read(git_port, "commit", selection.revision)
        headers, separator, _body = commit.partition(b"\n\n")
        header_lines = headers.split(b"\n")
        if (not separator
                or not header_lines
                or header_lines[0] !=
                   b"tree " + selection.source_tree.encode()
                or sum(line.startswith(b"tree ") for line in header_lines) != 1):
            raise Refused("v5-tree-commit")
        files = []
        for path, name in sorted(PATHS.items()):
            actual = git._blob_for_path(git_port, selection.source_tree, path)
            if actual != blobs[name]:
                raise Refused("v5-tree-membership")
            files.append((path, hashlib.sha256(actual).hexdigest()))
        if (git._scope(git_port, selection.repository_id,
                       ["contents:read", "metadata:read"], now) != git_scope
                or git._scope(identity_port, selection.repository_id,
                              ["metadata:read"], now) != identity_scope
                or original_selection != selection
                or original_prior != prior
                or original_blobs != blobs):
            raise Refused("v5-tree-drift")
        return TreeWitness(selection.revision, selection.source_tree,
                           tuple(files), identity_event_id)
    except Refused:
        raise
    except Exception:
        raise Refused("v5-tree-unavailable") from None
