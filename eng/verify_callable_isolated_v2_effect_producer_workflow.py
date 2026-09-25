"""Pure exact-byte guard for the disabled closed-scaffold producer proposal."""

from __future__ import annotations

import hashlib
import re

PINNED_WORKFLOW_SHA256 = "cb0183fe19b6b3a06e27f5112e4e65bbd8d7ceeac1c0d6693bcf260cb0be1820"
PINNED_WORKFLOW_BLOB_OID = "49dc5dc03ae143ef50b74100f8f9d7262ebf558d"
SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-producer-workflow-check/1"
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
REQUIRED = (
    "name: callable-isolated-v2-effect-release-proposal",
    "on:",
    "  workflow_dispatch:",
    "  produce-closed-scaffold:",
    "    if: ${{ false }}",
    "    runs-on: [self-hosted, linux, x64, isolated-v2-release-unselected]",
    "    environment: callable-isolated-v2-release-unselected",
    "        run: exit 78",
    "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
    "          persist-credentials: false",
    "          ref: ${{ github.sha }}",
    "          python3 -I -S eng/build_callable_isolated_v2_effect_scaffold.py \\",
    "          cmp -s work/fsc07-isolated-operator-v2/effect-scaffold-manifest.json \\",
    "        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
    "          name: fsgg-callable-isolated-v2-effect-scaffold.pyz",
    "          path: dist/fsgg-callable-isolated-v2-effect-scaffold.pyz",
    "          if-no-files-found: error",
    "          retention-days: 1",
    "          compression-level: 0",
)
FORBIDDEN = ("secrets.", "github.token", "pull_request:", "push:",
             "schedule:", "workflow_call:", "ubuntu-latest",
             "contents: write", "actions: write")


class Refused(ValueError):
    """Fixed refusal without workflow contents."""


def verify(raw: bytes, approved_sha256: str) -> dict[str, object]:
    """Accept only the reviewed disabled proposal; never dispatch."""
    if (type(raw) is not bytes or not 0 < len(raw) <= 16_384
            or type(approved_sha256) is not str
            or HEX64.fullmatch(approved_sha256) is None
            or approved_sha256 != PINNED_WORKFLOW_SHA256
            or hashlib.sha256(raw).hexdigest() != PINNED_WORKFLOW_SHA256
            or hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()
               != PINNED_WORKFLOW_BLOB_OID):
        raise Refused("producer-workflow-digest")
    try:
        text = raw.decode("ascii")
    except UnicodeError:
        raise Refused("producer-workflow-encoding") from None
    if not text.endswith("\n") or "\r" in text or "\x00" in text:
        raise Refused("producer-workflow-encoding")
    lines = text.splitlines()
    if (any(lines.count(line) != 1 for line in REQUIRED)
            or any(token in text for token in FORBIDDEN)
            or lines.count("  contents: read") != 1
            or lines.count("      contents: read") != 1
            or lines.count("    if: ${{ false }}") != 1
            or lines.count("        run: exit 78") != 1):
        raise Refused("producer-workflow-closed-shape")
    positions = [lines.index(line) for line in REQUIRED]
    if positions != sorted(positions):
        raise Refused("producer-workflow-step-order")
    return {"schema": SCHEMA, "workflowSha256": PINNED_WORKFLOW_SHA256,
            "workflowBlobOid": PINNED_WORKFLOW_BLOB_OID,
            "producerEnabled": False, "authorized": False,
            "canDispatch": False, "liveEffects": 0}
