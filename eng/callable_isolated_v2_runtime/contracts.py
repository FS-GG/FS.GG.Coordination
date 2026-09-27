"""Internal, immutable coordinates for the first GS2-09.9 /5 effect window.

These are trusted-composer inputs, not CLI arguments or evidence by themselves.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import importlib.util
import pathlib


@dataclass(frozen=True)
class Binding:
    source_revision: str
    source_tree: str
    artifact_sha256: str
    workflow_sha256: str
    target_repository: str
    target_repository_id: int
    target_prestate_sha256: str
    request_sha256: str
    run_id: int
    run_attempt: int
    approval_event_id: int
    issuer_actor_id: int
    journal_repository: str
    journal_ref: str
    journal_path: str
    journal_prior_generation: int
    journal_prior_head: str
    operation_id: str

    def claims(self) -> dict:
        return {"sourceRevision": self.source_revision,
                "sourceTree": self.source_tree,
                "artifactSha256": self.artifact_sha256,
                "workflowSha256": self.workflow_sha256,
                "targetRepository": self.target_repository,
                "targetRepositoryId": self.target_repository_id,
                "targetPrestateSha256": self.target_prestate_sha256,
                "requestSha256": self.request_sha256,
                "runId": self.run_id, "runAttempt": self.run_attempt,
                "approvalEventId": self.approval_event_id,
                "issuerActorId": self.issuer_actor_id,
                "journalRepository": self.journal_repository,
                "journalRef": self.journal_ref,
                "journalPath": self.journal_path,
                "journalPriorGeneration": self.journal_prior_generation,
                "journalPriorHead": self.journal_prior_head,
                "operationId": self.operation_id}


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def operator_module():
    """Load the retained /5 parser and classifier without editing its identity."""
    path = pathlib.Path(__file__).resolve().parents[1] / "callable-cli-isolated-operation-v2.py"
    spec = importlib.util.spec_from_file_location("callable_isolated_v2_retained_operator", path)
    module = importlib.util.module_from_spec(spec)
    import sys
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module
