"""Exact-byte validator for the source-only runtime candidate workflow."""

from __future__ import annotations

import hashlib
import re

SCHEMA = "fsgg.coordination.callable-isolated-v2-runtime-candidate-workflow/1"
WORKFLOW = ".github/workflows/callable-isolated-v2-runtime-candidate.yml"
PINNED_WORKFLOW_SHA256 = "aa848648cff0b78c120db3050b4b56ff167b703c003ca2d3aa64e7d64b0e9765"
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
REQUIRED = (
    "name: callable-isolated-v2-runtime-candidate",
    "on:",
    "  workflow_dispatch:",
    "permissions:",
    "  contents: read",
    "  build-runtime-candidate:",
    "    runs-on: ubuntu-latest",
    "      - name: Check out the exact workflow source",
    "        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1",
    "          persist-credentials: false",
    "          fetch-depth: 1",
    "          ref: ${{ github.sha }}",
    '          test "$(git rev-parse HEAD)" = "$GITHUB_SHA"',
    "          python3 eng/build_callable_isolated_v2_runtime.py \\",
    '            --output-directory "$CANDIDATE_OUTPUT"',
    "          python3 eng/verify_callable_isolated_v2_runtime_artifact.py \\",
    "            --archive \"$CANDIDATE_OUTPUT/callable-isolated-v2-runtime.pyz\" \\",
    '            --manifest "$CANDIDATE_OUTPUT/callable-isolated-v2-runtime-manifest.json"',
    "        uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
    "          name: callable-isolated-v2-runtime-candidate-${{ github.sha }}",
    "            ${{ runner.temp }}/callable-isolated-v2-runtime-candidate/callable-isolated-v2-runtime.pyz",
    "            ${{ runner.temp }}/callable-isolated-v2-runtime-candidate/callable-isolated-v2-runtime-manifest.json",
    "          if-no-files-found: error",
    "          retention-days: 1",
    "          compression-level: 0",
)
FORBIDDEN = (
    "secrets.", "github.token", "GITHUB_TOKEN", "contents: write",
    "actions: write", "id-token: write", "pull-requests: write", "push:",
    "pull_request:", "schedule:", "workflow_call:", "environment:",
    "self-hosted", "execute_installed", "recover_installed", "grant",
)


class Refused(ValueError):
    """Fixed refusal without returning candidate workflow contents."""


def verify(raw: bytes, expected_sha256: str) -> dict[str, object]:
    """Accept only the reviewed source-only workflow bytes."""
    if (type(raw) is not bytes or not 0 < len(raw) <= 16_384
            or type(expected_sha256) is not str
            or HEX64.fullmatch(expected_sha256) is None
            or expected_sha256 != PINNED_WORKFLOW_SHA256
            or hashlib.sha256(raw).hexdigest() != PINNED_WORKFLOW_SHA256):
        raise Refused("runtime-candidate-workflow-digest")
    try:
        text = raw.decode("ascii")
    except UnicodeError:
        raise Refused("runtime-candidate-workflow-encoding") from None
    if not text.endswith("\n") or "\r" in text or "\x00" in text:
        raise Refused("runtime-candidate-workflow-encoding")
    lines = text.splitlines()
    if (any(lines.count(line) != 1 for line in REQUIRED)
            or any(token in text for token in FORBIDDEN)
            or lines.count("  contents: read") != 1
            or lines.count("      contents: read") != 1):
        raise Refused("runtime-candidate-workflow-shape")
    positions = [lines.index(line) for line in REQUIRED]
    if positions != sorted(positions):
        raise Refused("runtime-candidate-workflow-order")
    return {
        "schema": SCHEMA,
        "workflowPath": WORKFLOW,
        "workflowSha256": PINNED_WORKFLOW_SHA256,
        "sourceOnly": True,
        "authorized": False,
        "canDispatchEffect": False,
        "liveEffects": 0,
    }
