"""Pure exact-byte guard for the held inspect-only v2 release workflow.

This is a draft source candidate, not an approved digest authority. It has no
filesystem, token, workflow API, journal or provider port. A future protected
owner must select a new immutable image and independently approve revised bytes.
"""

from __future__ import annotations

import dataclasses
import hashlib
import re
from typing import Any

WORKFLOW_SHA256 = "c486fe746ee88536897c277fc1cbcc320813c08d4b876dacb01adb78da759e93"
WORKFLOW_PATH = ".github/workflows/callable-isolated-v2-release.yml"
ZERO40 = "0" * 40
ZERO64 = "0" * 64
IMAGE = "ghcr.io/fs-gg/isolated-v2-release-unselected@sha256:" + ZERO64
SOURCE_HASHES = {
    "eng/build_callable_isolated_v2_zipapp.py": "2c581bed909f835ae24de9b79ab1e2a1d4b144dd5dcf3b8b3285872c4af6ed6b",
    "eng/callable_isolated_v2_entry.py": "03b0f43fffb265e1895e32ef76239e5076bbd22d4da690383b7479f915ab9adc",
    "eng/callable_isolated_v2_grant.py": "f37feb7dd835f3327fb1aac0987d3397ea19314cdfcf42099a0c490b48220da4",
    "eng/callable_isolated_v2_authority.py": "9a806bb78de25be887db0a0b9499f242d7989d33860e598e23e2ceff80172a26",
    "eng/verify_callable_isolated_v2_install.py": "0a581ccfef5cde4f360635e4ef352a85c648ea72b185e088f125c9d86e048237",
    "eng/callable_isolated_v2_runtime_closure.py": "938b71a2f35e3690e32bf81b788c23f95b2e188bc648835a2053eeffec9d6765",
    "eng/callable_isolated_v2_release_provenance.py": "caa51b61d3166060d31d25ec02082ba99cdb232af6a4b72ca45274f6aaec6515",
    "work/gs2-09-9-isolated-operator-rotation/zipapp-manifest.json": "9cf485b238a458c3234adafb6f3964b7e105507a8d8cfbeb01a1d067b2545fd3",
}
EMBEDDED = {
    "ARCHIVE_SHA256": "003f63ac1b0f895642a7000954607b0980f8e9f1dbb058576e66b40ed79cbfb7",
    "ZIPAPP_MANIFEST_SHA256": SOURCE_HASHES["work/gs2-09-9-isolated-operator-rotation/zipapp-manifest.json"],
    "BUILDER_SOURCE_SHA256": SOURCE_HASHES["eng/build_callable_isolated_v2_zipapp.py"],
    "ENTRY_SOURCE_SHA256": SOURCE_HASHES["eng/callable_isolated_v2_entry.py"],
    "GRANT_SOURCE_SHA256": SOURCE_HASHES["eng/callable_isolated_v2_grant.py"],
    "AUTHORITY_SOURCE_SHA256": SOURCE_HASHES["eng/callable_isolated_v2_authority.py"],
    "INSTALL_VERIFIER_SHA256": SOURCE_HASHES["eng/verify_callable_isolated_v2_install.py"],
    "CLOSURE_VERIFIER_SHA256": SOURCE_HASHES["eng/callable_isolated_v2_runtime_closure.py"],
    "PACKET_VERIFIER_SHA256": SOURCE_HASHES["eng/callable_isolated_v2_release_provenance.py"],
}
HEX64 = re.compile(r"[0-9a-f]{64}\Z")


class Refused(ValueError):
    """Fixed refusal code; no raw workflow or source content in errors."""


@dataclasses.dataclass(frozen=True)
class HeldWorkflow:
    workflow_sha256: str
    source_count: int
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def _held_boundary(body: str) -> None:
    """Independent semantic guard for a future pin refresh review."""
    if ("\r" in body or "\t" in body or "\x00" in body or not body.endswith("\n")
            or body.count("  workflow_dispatch:\n") != 1
            or body.count("permissions: {}\n") != 2
            or body.count("    if: ${{ false }}\n") != 1
            or body.count("      CANDIDATE_PARENT_HEAD: 5ffa942bb089ba3f376c13432730c9976aa72895\n") != 1
            or body.count("      DISALLOWED_LOCAL_CLOSURE_SHA256: 6783d094396af466461f3dfc8e8d0e41dada34b177e1c06dd784806c79979162\n") != 1
            or body.count("    environment: callable-isolated-v2-release\n") != 1
            or body.count("    runs-on: [self-hosted, linux, x64, isolated-v2-release-unselected]\n") != 1
            or body.count(f"      image: {IMAGE}\n") != 1
            or body.count("      RELEASE_STATE: unselected\n") != 1
            or body.count("    steps:\n") != 1
            or body.count("      - name:") != 1
            or body.count("        run: |\n") != 1
            or body.count("          exit 78\n") != 1
            or re.findall(r"^  ([A-Za-z][A-Za-z0-9_-]*):\n", body.split("\njobs:\n", 1)[-1], re.M) != ["inspect-release"]
            or any(value in body for value in
                   ("secrets.", "github.token", "GITHUB_TOKEN", "GH_TOKEN", "uses:",
                    "curl ", "gh api", " POST ", "pulls/"))):
        raise Refused("workflow-held-boundary")
    for key, digest_value in EMBEDDED.items():
        if body.count(f"      {key}: {digest_value}\n") != 1:
            raise Refused("workflow-source-pin")
    for key, value in {"EXPECTED_PROTECTED_SOURCE_REVISION": ZERO40,
                       "EXPECTED_PROTECTED_WORKFLOW_SHA256": ZERO64,
                       "EXPECTED_APPROVED_PACKET_SHA256": ZERO64,
                       "EXPECTED_RUNNER_ATTESTATION_SHA256": ZERO64,
                       "EXPECTED_PROTECTED_CLOSURE_SHA256": ZERO64}.items():
        if body.count(f"      {key}: '{value}'\n") != 1:
            raise Refused("workflow-unselected-pin")


def verify_held_template(raw: bytes, independently_expected_sha256: str,
                         source_bytes: dict[str, bytes]) -> HeldWorkflow:
    """Check a held template and exact stack bytes; never activate a job."""
    if (type(raw) is not bytes or len(raw) > 8_192
            or type(independently_expected_sha256) is not str
            or HEX64.fullmatch(independently_expected_sha256) is None):
        raise Refused("workflow-input")
    digest = hashlib.sha256(raw).hexdigest()
    if digest != independently_expected_sha256 or digest != WORKFLOW_SHA256:
        raise Refused("workflow-digest")
    try:
        body = raw.decode("utf-8")
    except UnicodeError:
        raise Refused("workflow-encoding") from None
    _held_boundary(body)
    if type(source_bytes) is not dict or set(source_bytes) != set(SOURCE_HASHES):
        raise Refused("workflow-source-set")
    for path, expected in SOURCE_HASHES.items():
        value: Any = source_bytes[path]
        if type(value) is not bytes or len(value) > 128_000 or hashlib.sha256(value).hexdigest() != expected:
            raise Refused("workflow-source-digest")
    return HeldWorkflow(digest, len(SOURCE_HASHES))
