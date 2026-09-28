"""Compose native candidate observation with the protected installed runtime.

This source boundary joins the independently authenticated, read-only S6
candidate observation to the S6/S7 protected host loader.  The host owns every
selection and expected identity.  Candidate bytes remain non-authoritative and
cannot supply an execution run, protected authority, journal, token, or write
port.
"""

from __future__ import annotations

import datetime as dt
from typing import Protocol

import callable_isolated_v2_runtime_candidate_read_adapter as candidate_read
import callable_isolated_v2_runtime_candidate_witness as candidate
import callable_isolated_v2_runtime_host as runtime_host


class Refused(ValueError):
    """Fixed refusal without reflecting candidate or protected host data."""


class InstalledHost(runtime_host.ProtectedHost, Protocol):
    """Host-provisioned installation and read-only candidate observation."""

    def candidate_selection(self) -> candidate.Selection: ...
    def candidate_observation_time(self) -> dt.datetime: ...
    def installed_workflow_bytes(self) -> bytes: ...
    def candidate_read_transport(self) -> candidate_read.ReadTransport: ...
    def candidate_download_transport(self) -> candidate_read.BundleTransport: ...


def _selection_bound(selection: object,
                     expected: object) -> candidate.Selection:
    if (type(selection) is not candidate.Selection
            or type(expected) is not runtime_host.ExpectedRuntime
            or (selection.repository_id, selection.source_sha,
                selection.source_tree, selection.run_id,
                selection.run_attempt, selection.artifact_id,
                selection.workflow_sha256)
               != (expected.repository_id, expected.source_revision,
                   expected.source_tree, expected.producer_run_id,
                   expected.producer_run_attempt, expected.artifact_id,
                   expected.workflow_sha256)):
        raise Refused("installed-candidate-selection")
    return selection


def observe_candidate(host: InstalledHost) -> candidate.CandidateWitness:
    """Derive the candidate witness entirely from host-selected native reads."""
    if host is None:
        raise Refused("installed-host-unavailable")
    try:
        expected = host.expected_runtime()
        selection = _selection_bound(host.candidate_selection(), expected)
        now = host.candidate_observation_time()
        workflow_bytes = host.installed_workflow_bytes()
        read_transport = host.candidate_read_transport()
        download_transport = host.candidate_download_transport()
    except Refused:
        raise
    except Exception:
        raise Refused("installed-host-unavailable") from None
    if (type(now) is not dt.datetime or now.utcoffset() != dt.timedelta(0)
            or type(workflow_bytes) is not bytes or not workflow_bytes
            or read_transport is None or download_transport is None
            or read_transport is download_transport):
        raise Refused("installed-candidate-observation")
    try:
        producer = candidate_read.NativeCandidateProducerAdapter(
            read_transport, selection, now)
        downloader = candidate_read.NativeCandidateDownloadAdapter(
            download_transport, selection.repository_id, now)
        return candidate.qualify(selection, workflow_bytes, producer,
                                 downloader, now)
    except (candidate.Refused, candidate_read.Refused):
        raise Refused("installed-candidate-unavailable") from None


def run(host: InstalledHost, *, recovery: bool = False):
    """Qualify the native candidate, then enter the protected installed host.

    The candidate phase has only read transports.  The protected host loader
    independently rereads expected identities and candidate bytes before it
    obtains the authority that can reach a journal, token, or provider write.
    """
    if type(recovery) is not bool:
        raise Refused("installed-mode")
    witnessed = observe_candidate(host)
    return runtime_host.run(host, witnessed, recovery=recovery)
