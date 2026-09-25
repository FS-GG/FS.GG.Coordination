"""Synthetic, no-I/O negative controls for a future protected readback."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import os
import pathlib
import socket
import sys
import unittest
from unittest.mock import patch

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
import callable_isolated_v2_release_provenance as release  # noqa: E402
import callable_isolated_v2_release_readback as readback  # noqa: E402
import validate_callable_isolated_v2_release_workflow as held  # noqa: E402

NOW = dt.datetime(2026, 9, 25, 12, 5, tzinfo=dt.timezone.utc)
INSTALLED_PATH = "/opt/fsgg/isolated-v2-release/fsgg-callable-isolated-v2.pyz"
INTERPRETER_PATH = "/opt/fsgg/python/bin/python3.14"


def canonical(value: dict) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True, allow_nan=False).encode("ascii")


def packet_fixture() -> dict:
    source = {"repository": "FS-GG/FS.GG.Coordination", "revision": "a" * 40,
              "tree": "b" * 40, "protectedMain": True,
              "files": copy.deepcopy(release.PINNED_SOURCE),
              "releaseVerifierSha256": "c" * 64}
    run = {"repository": source["repository"], "workflowPath": release.WORKFLOW,
           "revision": source["revision"], "ref": "refs/heads/main",
           "event": "workflow_dispatch", "runId": 201, "runAttempt": 1,
           "environment": release.ENVIRONMENT, "environmentId": 202,
           "actorId": 203, "executionTokenPresent": False}
    return {
        "schema": release.SCHEMA, "state": "prepared-not-authorized",
        "source": source,
        "workflow": {"repository": source["repository"], "path": release.WORKFLOW,
                     "revision": source["revision"], "sha256": "d" * 64},
        "artifact": {"name": release.ARCHIVE,
                     "archiveSha256": release.ARCHIVE_SHA256,
                     "manifestSha256": release.ZIPAPP_MANIFEST_SHA256,
                     "producerRunId": 204, "artifactId": 205,
                     "sourceRevision": source["revision"],
                     "downloadReadbackSha256": release.ARCHIVE_SHA256},
        "runner": {"image": "ghcr.io/fs-gg/isolated-v2-fixture@sha256:" + "e" * 64,
                   "platform": "linux/amd64", "imageAttestationSha256": "f" * 64,
                   "ephemeral": True, "readOnlyRoot": True},
        "runtime": {"interpreterPath": INTERPRETER_PATH,
                    "interpreterSha256": "1" * 64, "interpreterVersion": "3.14.7",
                    "flags": ["-I", "-S"], "closureManifestSha256": "2" * 64,
                    "stdlibTreeSha256": "3" * 64, "mappedFileCount": 17},
        "run": run,
        "approval": {"schema": release.APPROVAL_SCHEMA, "complete": True,
                     "approvalId": 206, "runId": run["runId"],
                     "runAttempt": run["runAttempt"],
                     "environmentId": run["environmentId"], "reviewerId": 207,
                     "reviewerMembership": "active", "approvedAt": "2026-09-25T12:00:00Z",
                     "expiresAt": "2026-09-25T12:20:00Z"},
        "permissions": copy.deepcopy(release.PERMISSIONS),
    }


def selection_fixture(packet: dict) -> dict:
    return {
        "schema": readback.SCHEMA, "state": "selected-not-authorized",
        "sourceRevision": packet["source"]["revision"],
        "sourceTree": packet["source"]["tree"],
        "workflowSha256": packet["workflow"]["sha256"],
        "archiveSha256": packet["artifact"]["archiveSha256"],
        "manifestSha256": packet["artifact"]["manifestSha256"],
        "producerRunId": packet["artifact"]["producerRunId"],
        "artifactId": packet["artifact"]["artifactId"],
        "runnerImage": packet["runner"]["image"],
        "imageAttestationSha256": packet["runner"]["imageAttestationSha256"],
        "interpreterSha256": packet["runtime"]["interpreterSha256"],
        "closureManifestSha256": packet["runtime"]["closureManifestSha256"],
        "stdlibTreeSha256": packet["runtime"]["stdlibTreeSha256"],
        "packetSha256": hashlib.sha256(canonical(packet)).hexdigest(),
        "runId": packet["run"]["runId"],
        "runAttempt": packet["run"]["runAttempt"],
        "environmentId": packet["run"]["environmentId"],
        "actorId": packet["run"]["actorId"],
        "approvalId": packet["approval"]["approvalId"],
        "reviewerId": packet["approval"]["reviewerId"],
    }


def controls_fixture(selected: dict, interpreter_path: str = INTERPRETER_PATH) -> dict:
    command = [interpreter_path, "-I", "-S", INSTALLED_PATH]
    return {
        "schema": readback.CONTROLS_SCHEMA, "complete": True,
        "runId": selected["runId"], "runAttempt": selected["runAttempt"],
        "sourceRevision": selected["sourceRevision"],
        "sourceTree": selected["sourceTree"],
        "actorId": selected["actorId"],
        "producerRunId": selected["producerRunId"],
        "artifactId": selected["artifactId"],
        "workflowSha256": selected["workflowSha256"],
        "archiveSha256": selected["archiveSha256"],
        "interpreterSha256": selected["interpreterSha256"],
        "closureManifestSha256": selected["closureManifestSha256"],
        "installedArchivePostSha256": selected["archiveSha256"],
        "interpreterPostSha256": selected["interpreterSha256"],
        "closurePostSha256": selected["closureManifestSha256"],
        "installedArchivePath": INSTALLED_PATH,
        "noGrant": {"argv": command + ["inspect-grant"], "exitCode": 2,
                    "stdoutSha256": readback.EMPTY_SHA256,
                    "stderrSha256": readback.NO_GRANT_STDERR_SHA256},
        "unknownCommand": {"argv": command + ["execute-native-pull"], "exitCode": 2,
                           "stdoutSha256": readback.EMPTY_SHA256,
                           "stderrSha256": readback.UNKNOWN_STDERR_SHA256},
        "executionTokenPresent": False, "providerRequestCount": 0,
        "journalWriteCount": 0, "workingDirectoryWriteCount": 0,
        "observedAt": "2026-09-25T12:03:00Z",
    }


def review_fixture(selected: dict) -> dict:
    return {"schema": readback.REVIEW_SCHEMA, "complete": True,
            "selectionSha256": hashlib.sha256(canonical(selected)).hexdigest(),
            "bindings": copy.deepcopy(selected),
            "repository": "FS-GG/FS.GG.Coordination",
            "workflowPath": release.WORKFLOW,
            "sourceRevision": selected.get("sourceRevision"),
            "runId": selected.get("runId"), "runAttempt": selected.get("runAttempt"),
            "environmentId": selected.get("environmentId"),
            "actorId": selected.get("actorId"),
            "approvalId": selected.get("approvalId"),
            "reviewerId": selected.get("reviewerId", 0), "reviewerMembership": "active",
            "reviewEventId": 208, "reviewedAt": "2026-09-25T12:04:00Z"}


class ReleaseObserver:
    def __init__(self, packet: dict):
        self.packet = packet

    def read_current(self) -> dict:
        return {"schema": release.OBSERVATION_SCHEMA, "complete": True,
                **{key: copy.deepcopy(self.packet[key]) for key in
                   ("source", "workflow", "artifact", "runner", "runtime", "run", "permissions")}}


class ApprovalObserver:
    def __init__(self, packet: dict):
        self.packet = packet

    def read_approval(self) -> dict:
        return copy.deepcopy(self.packet["approval"])


class ControlsObserver:
    def __init__(self, value: dict):
        self.value = value

    def read_controls(self) -> dict:
        return copy.deepcopy(self.value)


class ReviewObserver:
    def __init__(self, value: dict):
        self.value = value

    def read_review(self) -> dict:
        return copy.deepcopy(self.value)


def verify(packet: dict, selected: dict | None = None, controls: dict | None = None,
           observed: dict | None = None, approved: dict | None = None,
           reviewed: dict | None = None):
    selection = selection_fixture(packet) if selected is None else selected
    control_value = (controls_fixture(selection, packet["runtime"]["interpreterPath"])
                     if controls is None else controls)
    raw_selection = canonical(selection)
    return readback.verify_selected_readback(
        raw_selection, hashlib.sha256(raw_selection).hexdigest(), canonical(packet),
        ReleaseObserver(packet if observed is None else observed),
        ApprovalObserver(packet if approved is None else approved),
        ControlsObserver(control_value), NOW,
        ReviewObserver(review_fixture(selection) if reviewed is None else reviewed))


class SelectedReadbackTests(unittest.TestCase):
    def test_installed_refusal_names_exact_interpreter_and_archive(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        controls = controls_fixture(selected)
        # The old record can claim a refusal without naming the executable,
        # isolation flags, or archive that actually received the command.
        del controls["installedArchivePath"]
        with self.assertRaisesRegex(readback.Refused, "controls-shape"):
            verify(packet, controls=controls)
        for key, replacement in (
                ("installedArchivePath", "relative/fsgg-callable-isolated-v2.pyz"),
                ("installedArchivePath", "/opt/fsgg/../fsgg-callable-isolated-v2.pyz"),
                ("installedArchivePath", "/opt/fsgg/other.pyz")):
            with self.subTest(key=key, replacement=replacement):
                controls = controls_fixture(selected)
                controls[key] = replacement
                with self.assertRaisesRegex(readback.Refused, "controls-archive-path"):
                    verify(packet, controls=controls)
        for argv in (
                ["inspect-grant"],
                ["/usr/bin/python3", "-I", "-S", INSTALLED_PATH, "inspect-grant"],
                [INTERPRETER_PATH, "-S", INSTALLED_PATH, "inspect-grant"],
                [INTERPRETER_PATH, "-I", "-S", "/tmp/other.pyz", "inspect-grant"]):
            with self.subTest(argv=argv):
                controls = controls_fixture(selected)
                controls["noGrant"]["argv"] = argv
                with self.assertRaisesRegex(readback.Refused, "control-refusal"):
                    verify(packet, controls=controls)

    def test_review_event_must_name_its_own_protected_origin(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        baseline = review_fixture(selected)
        # An otherwise exact selection supplied by an unrelated event is not
        # evidence that this run's distinct reviewer approved it.
        del baseline["runId"]
        with self.assertRaisesRegex(readback.Refused, "selection-review-shape"):
            verify(packet, reviewed=baseline)
        for key, replacement in (
                ("repository", "FS-GG/other"),
                ("workflowPath", ".github/workflows/other.yml"),
                ("sourceRevision", "f" * 40),
                ("runId", 999), ("runAttempt", 2),
                ("environmentId", 999), ("actorId", selected["reviewerId"]),
                ("approvalId", 999), ("runId", True)):
            with self.subTest(key=key, replacement=replacement):
                review = review_fixture(selected)
                review[key] = replacement
                with self.assertRaisesRegex(readback.Refused, "selection-review-binding"):
                    verify(packet, reviewed=review)

    def test_review_must_follow_installed_control_observation(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        controls = controls_fixture(selected)
        del controls["observedAt"]
        with self.assertRaisesRegex(readback.Refused, "controls-shape"):
            verify(packet, controls=controls)
        for controls_time, review_time in (
                ("2026-09-25T11:59:59Z", "2026-09-25T12:04:00Z"),
                ("2026-09-25T12:06:00Z", "2026-09-25T12:07:00Z"),
                ("2026-09-25T12:04:00Z", "2026-09-25T12:03:59Z"),
                ("2026-09-25T12:04:00Z", "2026-09-25T12:04:00Z")):
            with self.subTest(controls_time=controls_time, review_time=review_time):
                controls = controls_fixture(selected)
                controls["observedAt"] = controls_time
                review = review_fixture(selected)
                review["reviewedAt"] = review_time
                with self.assertRaises(readback.Refused):
                    verify(packet, controls=controls, reviewed=review)

    def test_self_sealed_selection_without_independent_review_refuses(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        raw = canonical(selected)
        with self.assertRaisesRegex(readback.Refused, "selection-review-custody"):
            readback.verify_selected_readback(
                raw, hashlib.sha256(raw).hexdigest(), canonical(packet),
                ReleaseObserver(packet), ApprovalObserver(packet),
                ControlsObserver(controls_fixture(selected)), NOW)

    def test_selection_review_must_independently_bind_actor_artifact_runner_and_time(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        baseline = review_fixture(selected)
        for key, replacement in (("complete", False), ("selectionSha256", "0" * 64),
                                 ("reviewerId", selected["actorId"]),
                                 ("reviewerMembership", "unknown"),
                                 ("reviewEventId", selected["approvalId"]),
                                 ("reviewedAt", "2026-09-25T11:59:59Z"),
                                 ("reviewedAt", "2026-09-25T12:06:00Z")):
            with self.subTest(key=key, replacement=replacement):
                review = copy.deepcopy(baseline)
                review[key] = replacement
                with self.assertRaises(readback.Refused):
                    verify(packet, reviewed=review)
        for key, replacement in (("sourceTree", "f" * 40), ("actorId", 999),
                                 ("producerRunId", 999), ("artifactId", 999),
                                 ("runnerImage", "ghcr.io/fs-gg/other@sha256:" + "9" * 64),
                                 ("imageAttestationSha256", "9" * 64),
                                 ("closureManifestSha256", "9" * 64),
                                 ("packetSha256", "9" * 64)):
            with self.subTest(binding=key):
                review = copy.deepcopy(baseline)
                review["bindings"][key] = replacement
                with self.assertRaisesRegex(readback.Refused, "selection-review-binding"):
                    verify(packet, reviewed=review)
        review = copy.deepcopy(baseline)
        del review["reviewEventId"]
        with self.assertRaisesRegex(readback.Refused, "selection-review-shape"):
            verify(packet, reviewed=review)

    def test_selection_review_port_must_be_distinct_and_available(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        raw = canonical(selected)
        release_port = ReleaseObserver(packet)
        approval_port = ApprovalObserver(packet)
        controls_port = ControlsObserver(controls_fixture(selected))
        with self.assertRaisesRegex(readback.Refused, "selection-review-custody"):
            readback.verify_selected_readback(
                raw, hashlib.sha256(raw).hexdigest(), canonical(packet),
                release_port, approval_port, controls_port, NOW, controls_port)

        class UnavailableReview:
            def read_review(self):
                raise RuntimeError("unavailable")

        with self.assertRaisesRegex(readback.Refused, "selection-review-unavailable"):
            readback.verify_selected_readback(
                raw, hashlib.sha256(raw).hexdigest(), canonical(packet),
                release_port, approval_port, controls_port, NOW, UnavailableReview())

    def test_complete_synthetic_readback_is_non_authorizing_and_has_no_io(self):
        packet = packet_fixture()
        with patch("builtins.open", side_effect=AssertionError("file or journal IO")), \
             patch.object(os, "getenv", side_effect=AssertionError("token read")), \
             patch.object(socket, "socket", side_effect=AssertionError("provider")):
            evidence = verify(packet)
        self.assertEqual(packet["source"]["revision"], evidence.source_revision)
        self.assertFalse(evidence.authorized)
        self.assertFalse(evidence.can_dispatch)
        self.assertEqual(0, evidence.live_effects)

    def test_candidate_revision_claim_remains_only_non_authorizing_consistency(self):
        packet = packet_fixture()
        candidate_head = "374c49175b1031b30a379220f353a6f272e75f57"
        packet["source"]["revision"] = candidate_head
        packet["workflow"]["revision"] = candidate_head
        packet["artifact"]["sourceRevision"] = candidate_head
        packet["run"]["revision"] = candidate_head
        evidence = verify(packet)
        self.assertEqual(candidate_head, evidence.source_revision)
        self.assertFalse(evidence.authorized)
        self.assertFalse(evidence.can_dispatch)
        self.assertEqual(0, evidence.live_effects)

    def test_wrong_draft_head_held_workflow_mutable_runner_and_wrong_pin_refuse(self):
        packet = packet_fixture()
        mutations = {
            "sourceRevision": readback.DRAFT_HEAD,
            "workflowSha256": held.WORKFLOW_SHA256,
            "runnerImage": "ubuntu-latest",
            "imageAttestationSha256": "0" * 64,
            "archiveSha256": "0" * 64,
            "closureManifestSha256": release.LOCAL_CLOSURE_SHA256,
            "packetSha256": "0" * 64,
        }
        for key, value in mutations.items():
            with self.subTest(key=key):
                selected = selection_fixture(packet)
                selected[key] = value
                with self.assertRaises(readback.Refused):
                    verify(packet, selected)
        selected = selection_fixture(packet)
        selected["runnerImage"] = "ghcr.io/fs-gg/isolated-v2@sha256:" + "0" * 64
        with self.assertRaisesRegex(readback.Refused, "selection-unselected"):
            verify(packet, selected)
        selected = selection_fixture(packet)
        selected["sourceRevision"] = "f" * 40
        with self.assertRaisesRegex(readback.Refused, "selected-binding"):
            verify(packet, selected)
        selected = selection_fixture(packet)
        selected["sourceTree"] = "f" * 40
        with self.assertRaisesRegex(readback.Refused, "selected-binding"):
            verify(packet, selected)
        observed = packet_fixture()
        observed["source"]["revision"] = "f" * 40
        with self.assertRaisesRegex(readback.Refused, "protected-binding"):
            verify(packet, observed=observed)

    def test_historical_disabled_workflow_digest_cannot_be_selected(self):
        for digest in (readback.ORIGINAL_HELD_WORKFLOW_SHA256,
                       readback.REFRESHED_HELD_WORKFLOW_SHA256):
            with self.subTest(digest=digest):
                packet = packet_fixture()
                packet["workflow"]["sha256"] = digest
                with self.assertRaisesRegex(readback.Refused, "selection-unselected"):
                    verify(packet)

    def test_resealed_packet_cannot_swap_actor_run_artifact_or_image_selection(self):
        baseline = packet_fixture()
        selected = selection_fixture(baseline)
        changes = [
            ("run", "actorId", 999),
            ("run", "runId", 999),
            ("run", "runAttempt", 2),
            ("artifact", "producerRunId", 999),
            ("artifact", "artifactId", 999),
            ("runner", "image", "ghcr.io/fs-gg/other@sha256:" + "9" * 64),
        ]
        for section, key, replacement in changes:
            with self.subTest(section=section, key=key):
                packet = copy.deepcopy(baseline)
                packet[section][key] = replacement
                if key in ("runId", "runAttempt"):
                    packet["approval"][key] = replacement
                selected_copy = copy.deepcopy(selected)
                selected_copy["packetSha256"] = hashlib.sha256(canonical(packet)).hexdigest()
                with self.assertRaisesRegex(readback.Refused, "selected-binding"):
                    verify(packet, selected_copy)
        for key in ("actorId", "producerRunId", "artifactId"):
            with self.subTest(missing_selected=key):
                selected_copy = copy.deepcopy(selected)
                del selected_copy[key]
                with self.assertRaisesRegex(readback.Refused, "selection-shape"):
                    verify(baseline, selected_copy, controls_fixture(selected))
            with self.subTest(boolean_selected=key):
                selected_copy = copy.deepcopy(selected)
                selected_copy[key] = True
                with self.assertRaisesRegex(readback.Refused, "selection-unselected"):
                    verify(baseline, selected_copy)

    def test_absent_or_non_distinct_reviewer_refuses(self):
        packet = packet_fixture()
        missing = copy.deepcopy(packet)
        del missing["approval"]["reviewerId"]
        selected_missing = selection_fixture(packet)
        selected_missing["packetSha256"] = hashlib.sha256(canonical(missing)).hexdigest()
        with self.assertRaises(readback.Refused):
            verify(missing, selected_missing)
        same_actor = copy.deepcopy(packet)
        same_actor["approval"]["reviewerId"] = same_actor["run"]["actorId"]
        with self.assertRaises(readback.Refused):
            verify(same_actor)
        observed = copy.deepcopy(packet)
        observed["approval"]["complete"] = False
        with self.assertRaisesRegex(readback.Refused, "protected-binding"):
            verify(packet, approved=observed)
        selected = selection_fixture(packet)
        del selected["reviewerId"]
        with self.assertRaisesRegex(readback.Refused, "selection-shape"):
            verify(packet, selected)
        selected = selection_fixture(packet)
        selected["actorId"] = selected["reviewerId"]
        with self.assertRaisesRegex(readback.Refused, "selection-unselected"):
            verify(packet, selected)

    def test_incomplete_or_effectful_control_readback_refuses(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        baseline = controls_fixture(selected)
        changes = [
            ("complete", False), ("executionTokenPresent", True),
            ("providerRequestCount", 1), ("journalWriteCount", 1),
            ("workingDirectoryWriteCount", 1),
            ("runAttempt", True),
            ("installedArchivePostSha256", "0" * 64),
            ("closurePostSha256", "0" * 64),
        ]
        for key, value in changes:
            with self.subTest(key=key):
                controls = copy.deepcopy(baseline)
                controls[key] = value
                with self.assertRaises(readback.Refused):
                    verify(packet, selected, controls)
        for key in ("noGrant", "unknownCommand", "runAttempt", "closurePostSha256"):
            with self.subTest(missing=key):
                controls = copy.deepcopy(baseline)
                del controls[key]
                with self.assertRaisesRegex(readback.Refused, "controls-shape"):
                    verify(packet, selected, controls)
        for command in ("noGrant", "unknownCommand"):
            with self.subTest(command=command):
                controls = copy.deepcopy(baseline)
                controls[command]["exitCode"] = 0
                with self.assertRaisesRegex(readback.Refused, "control-refusal"):
                    verify(packet, selected, controls)
            with self.subTest(stderr=command):
                controls = copy.deepcopy(baseline)
                controls[command]["stderrSha256"] = "0" * 64
                with self.assertRaisesRegex(readback.Refused, "control-refusal"):
                    verify(packet, selected, controls)

    def test_installed_controls_bind_tree_actor_and_producer_identity(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        baseline = controls_fixture(selected)
        for key in ("sourceRevision", "sourceTree", "actorId", "producerRunId",
                    "artifactId"):
            with self.subTest(missing=key):
                controls = copy.deepcopy(baseline)
                del controls[key]
                with self.assertRaisesRegex(readback.Refused, "controls-shape"):
                    verify(packet, selected, controls)
            with self.subTest(swapped=key):
                controls = copy.deepcopy(baseline)
                controls[key] = "f" * 40 if key.startswith("source") else 999
                with self.assertRaisesRegex(readback.Refused, "controls-incomplete"):
                    verify(packet, selected, controls)

    def test_selection_digest_duplicate_foreign_and_custody_refuse(self):
        packet = packet_fixture()
        selected = selection_fixture(packet)
        raw = canonical(selected)
        release_port = ReleaseObserver(packet)
        approval_port = ApprovalObserver(packet)
        controls_port = ControlsObserver(controls_fixture(selected))
        with self.assertRaisesRegex(readback.Refused, "selection-digest"):
            readback.verify_selected_readback(raw, "0" * 64, canonical(packet),
                                              release_port, approval_port, controls_port, NOW)
        duplicate = raw.replace(b'"state":"selected-not-authorized"',
                                b'"state":"selected-not-authorized","state":"selected-not-authorized"', 1)
        with self.assertRaisesRegex(readback.Refused, "duplicate-member"):
            readback.verify_selected_readback(duplicate, hashlib.sha256(duplicate).hexdigest(),
                                              canonical(packet), release_port, approval_port,
                                              controls_port, NOW)
        selected["foreign"] = True
        with self.assertRaisesRegex(readback.Refused, "selection-shape"):
            verify(packet, selected)
        with self.assertRaisesRegex(readback.Refused, "observer-custody"):
            readback.verify_selected_readback(raw, hashlib.sha256(raw).hexdigest(),
                                              canonical(packet), release_port, approval_port,
                                              release_port, NOW)


if __name__ == "__main__":
    unittest.main()
