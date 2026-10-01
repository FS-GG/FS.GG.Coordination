#!/usr/bin/env python3
"""Focused fail-closed contract for the qualified 0.2.0 publication pins."""

from __future__ import annotations

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github/workflows/callable-cli-release-publish.yml"

EXPECTED = {
    "EXPECTED_OPERATION": "publish-v2-lang-01-2-cli-020",
    "EXPECTED_SOURCE": "d25b9eaec991c94593adcecda6869d07dabdfb43",
    "EXPECTED_TREE": "169b7df260ee6668b8b28d43857183ad669b0e12",
    "EXPECTED_MERGE": "d25b9eaec991c94593adcecda6869d07dabdfb43",
    "EXPECTED_SHA256": "8ee67f83cecb3898eee12fd69f54cad0e3d1e232b3c88c18c434e3969019ab13",
    "EXPECTED_BUNDLE_SHA256": "c4ccc949ba02d67eba27302dfdffa258ecd4a55b628315fff82c26b2e4566abc",
    "EXPECTED_IMAGE_SHA256": "24dfd6fbf5e5d86b664963e5bcf896f2bbaba8803fdd9125c343a9389d6295b6",
    "EXPECTED_PORTABLE_MANIFEST_SHA256": "bd08411e277c77f0f607716c510cc487ac438e4aeeff766b6b76e8a63e49d390",
    "EXPECTED_PREPARATION_RUN_ID": "36794564231",
    "EXPECTED_PREPARATION_ARTIFACT_ID": "11133062598",
    "EXPECTED_PREPARATION_ARCHIVE_SHA256": "27c52b6cc7aeb415be0c313fda40165c83daf1e30b5fe8e89d16cb9828824aa9",
}


def require_in_order(text: str, fragments: list[str]) -> None:
    position = -1
    for fragment in fragments:
        next_position = text.find(fragment, position + 1)
        assert next_position >= 0, f"missing workflow gate: {fragment}"
        assert next_position > position
        position = next_position


def main() -> None:
    text = WORKFLOW.read_text(encoding="utf-8")
    pins = dict(re.findall(r"^      (EXPECTED_[A-Z0-9_]+): ([^\n]+)$", text, re.MULTILINE))
    assert pins == EXPECTED

    assert 'test "$OPERATION" = "$EXPECTED_OPERATION"' in text
    assert 'test "$REQUESTED_SOURCE" = "$EXPECTED_SOURCE"' in text
    assert 'test "$REQUESTED_MERGE" = "$EXPECTED_MERGE"' in text
    assert 'test "$REQUESTED_SHA256" = "$EXPECTED_SHA256"' in text
    assert 'test "$(git rev-parse "$EXPECTED_SOURCE^{tree}")" = "$EXPECTED_TREE"' in text
    assert 'test "$(git rev-parse "$EXPECTED_MERGE^{tree}")" = "$EXPECTED_TREE"' in text

    assert '.conclusion == "success"' in text
    assert '.workflow_run.id == $run and .workflow_run.head_sha == $source' in text
    assert '--arg name "callable-cli-$EXPECTED_SOURCE"' in text
    assert '--arg digest "sha256:$EXPECTED_PREPARATION_ARCHIVE_SHA256"' in text

    for digest in (
        "EXPECTED_SHA256",
        "EXPECTED_BUNDLE_SHA256",
        "EXPECTED_IMAGE_SHA256",
        "EXPECTED_PORTABLE_MANIFEST_SHA256",
    ):
        assert f'= "${digest}"' in text
    assert '.publicationAuthorized == false and .tagAuthorized == false and .activationAuthorized == false' in text
    assert 'cmp "$CANDIDATE_OUTPUT/$PACKAGE_FILE" "$READBACK_OUTPUT/reproduced/$PACKAGE_FILE"' in text

    first_effect = text.index("- name: Publish to GitHub Packages first")
    qualification = text.index('.remainingExecutionRoots == 0')
    collision = text.index("- name: Observe nuget.org and validate recoverable ordering before either effect")
    provenance = text.index("- name: Verify provenance and installed schemas before publication")
    assert qualification < provenance < collision < first_effect

    require_in_order(
        text,
        [
            "- name: Publish to GitHub Packages first",
            "- name: Verify exact bytes served by GitHub Packages",
            "- name: Publish the same retained bytes to nuget.org",
            "- name: Read back nuget.org and verify payload identity",
            "- name: Create immutable tag and GitHub release only after both feeds settle",
        ],
    )
    assert text.count('dotnet nuget push "$CANDIDATE_OUTPUT/$PACKAGE_FILE"') == 2
    assert 'test "$(sha256sum "$READBACK_OUTPUT/github/$PACKAGE_FILE" | cut -d\' \' -f1)" = "$EXPECTED_SHA256"' in text
    assert 'verify-callable-cli-served.py "$CANDIDATE_OUTPUT/$PACKAGE_FILE" "$READBACK_OUTPUT/nuget/$PACKAGE_FILE"' in text

    anonymous = text[
        text.index("- name: Anonymous public install and schema readback") :
        text.index("- name: Record dual-feed and portable-asset readback")
    ]
    require_in_order(
        anonymous,
        [
            "installed=false",
            "for attempt in {1..60}; do",
            'rm -rf -- "$RUNNER_TEMP/public-tool"',
            'install_log="$READBACK_OUTPUT/public-install-$attempt.log"',
            'dotnet tool install "$PACKAGE_ID" --version "$PACKAGE_VERSION"',
            'grep -F "Version $PACKAGE_VERSION of package ${PACKAGE_ID,,} is not found in NuGet feeds https://api.nuget.org/v3/index.json."',
            'test "$attempt" != 60',
            "sleep 10",
            'test "$installed" = true',
        ],
    )
    assert anonymous.count("dotnet tool install") == 1
    assert "--configfile \"$config\" --no-cache" in anonymous
    assert "|| true" not in anonymous

    assert "PACKAGE_VERSION: 0.2.0" in text
    assert "PACKAGE_FILE: FS.GG.Coordination.Cli.0.2.0.nupkg" in text
    assert "PORTABLE_BUNDLE: portable-workspace-v1-0.2.0.zip" in text
    assert "PORTABLE_IMAGE: portable-workspace-linux-amd64-0.2.0.oci.tar" in text
    assert "0.2.1" not in text

    cleanup = text.index("- name: Remove exact private runtime state")
    assert cleanup > first_effect
    assert 'rm -rf -- "$PUBLISH_ROOT" "$PUBLISH_RUNROOT" "$PUBLISH_STATE"' in text
    assert 'test ! -e "$PUBLISH_ROOT" && test ! -e "$PUBLISH_RUNROOT" && test ! -e "$PUBLISH_STATE"' in text


if __name__ == "__main__":
    main()
