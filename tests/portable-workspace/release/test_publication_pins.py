#!/usr/bin/env python3
"""Focused fail-closed contract for the qualified 0.2.1 publication pins."""

from __future__ import annotations

import re
import json
import os
import tempfile
import urllib.error
import urllib.request
from unittest.mock import patch
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github/workflows/callable-cli-release-publish.yml"

EXPECTED = {
    "EXPECTED_OPERATION": "publish-v2-diag-01-4-cli-021",
    "EXPECTED_SOURCE": "bc55a3d1cc887d653c1bb1198e4823b24f47aaa8",
    "EXPECTED_TREE": "723919d52ee2598858907978b1f33332ab1d9074",
    "EXPECTED_MERGE": "bc55a3d1cc887d653c1bb1198e4823b24f47aaa8",
    "EXPECTED_SHA256": "3b4381be04a47d48016b2b99d8855f7072124dd7409beb23ec766ceec99ef165",
    "EXPECTED_BUNDLE_SHA256": "a7bc5186a7f5c4f5fd54153d773c2ddee6ca8701979f2fc211a50bf51153c67f",
    "EXPECTED_IMAGE_SHA256": "656a82fdd79a39ed977253db611d3ea8e2c2231fba12ccca78d63efd743da365",
    "EXPECTED_PORTABLE_MANIFEST_SHA256": "c25dbfc651b7f45cf3f05cc1ac500ce1365aa6ffae5a2713077960544eb9751a",
    "EXPECTED_PREPARATION_RUN_ID": "37403093489",
    "EXPECTED_PREPARATION_ARTIFACT_ID": "11386361825",
    "EXPECTED_PREPARATION_ARCHIVE_SHA256": "269a75efe766321effd54a9a0affa701a51ee25e401c9773f42d8e895f12e67c",
}


def require_in_order(text: str, fragments: list[str]) -> None:
    position = -1
    for fragment in fragments:
        next_position = text.find(fragment, position + 1)
        assert next_position >= 0, f"missing workflow gate: {fragment}"
        assert next_position > position
        position = next_position



def verify_preflight(text: str) -> None:
    preflight = text[text.index("  preflight:\n"):text.index("  publish:\n")]
    assert "if: inputs.preflight_only == true" in preflight
    assert "  publish:\n    if: inputs.preflight_only != true" in text
    assert "preflight_only:\n" in text and "default: false\n        type: boolean" in text
    assert "packages: read" in preflight and "id-token: write" in preflight
    assert "packages: write" not in preflight and "contents: write" not in preflight
    assert preflight.count("uses:") == 2
    assert "NuGet/login@8d196754b4036150537f80ac539e15c2f1028841" in preflight
    assert "actions/upload-artifact@" in preflight
    for forbidden in ("dotnet ", "gh ", "checkout@", "NUGET_API_KEY", "outputs.", "git push", "nuget push"):
        assert forbidden not in preflight, forbidden
    require_in_order(preflight, ["Observe exact main", "Verify existing Trusted Publishing", "Record successful authorization"])
    code = preflight.split("          python3 - <<'PY_PREFLIGHT'\n", 1)[1].split("          PY_PREFLIGHT\n", 1)[0]
    code = "\n".join(line[10:] for line in code.splitlines())
    compile(code, "actual-workflow-preflight", "exec")
    source = "a" * 40
    namespace = "https://api.github.com/orgs/FS-GG/packages/nuget/fs.gg.coordination.cli"

    class Response:
        status = 200
        def __init__(self, value): self.raw = json.dumps(value).encode()
        def __enter__(self): return self
        def __exit__(self, *args): return False
        def read(self, limit): return self.raw[:limit]

    for mode in ("good", "org-denied", "org-collision", "public-collision", "incomplete", "malformed", "main-moved"):
        main_reads = 0
        def open_response(request, timeout):
            nonlocal main_reads
            assert timeout == 15
            url = request.full_url
            if url.startswith("https://api.github.com/"):
                assert request.get_header("Authorization") == "Bearer fixture-only-token"
            else:
                assert request.get_header("Authorization") is None
            if url.endswith("/git/ref/heads/main"):
                main_reads += 1
                return Response({"object": {"sha": "b" * 40 if mode == "main-moved" and main_reads == 2 else source}})
            if url == namespace:
                if mode == "org-denied": raise urllib.error.HTTPError(url, 403, "fixture refusal", {}, None)
                return Response({"name": "FS.GG.Coordination.Cli", "package_type": "nuget"})
            if url.startswith(namespace + "/versions?"):
                if mode == "incomplete": return Response([{"name": str(i)} for i in range(100)])
                if mode == "malformed": return Response([{"name": 3}])
                return Response([{"name": "0.2.2" if mode == "org-collision" else "0.2.0"}])
            assert url == "https://api.nuget.org/v3-flatcontainer/fs.gg.coordination.cli/index.json"
            return Response({"versions": ["0.2.2" if mode == "public-collision" else "0.2.0"]})
        with tempfile.TemporaryDirectory() as directory:
            environment = {"REQUESTED_SOURCE": source, "GITHUB_SHA": source, "GITHUB_REF": "refs/heads/main",
                           "GITHUB_REPOSITORY": "FS-GG/FS.GG.Coordination", "GH_TOKEN": "fixture-only-token", "RUNNER_TEMP": directory}
            refused = False
            with patch.dict(os.environ, environment, clear=True), patch.object(urllib.request, "urlopen", open_response):
                try: exec(code, {})
                except (SystemExit, urllib.error.HTTPError): refused = True
            receipt = Path(directory) / "migration-release-preflight.json"
            assert refused == (mode != "good"), mode
            assert receipt.exists() == (mode == "good"), mode
            if receipt.exists():
                value = json.loads(receipt.read_text())
                assert value["githubPaginationComplete"] and value["publicVersionAbsent"]
                assert value["versionReserved"] is False and value["publicationAuthorized"] is False
                assert "fixture-only-token" not in receipt.read_text()
                assert "trustedPublishingLoginSucceeded" not in value


def main() -> None:
    text = WORKFLOW.read_text(encoding="utf-8")
    verify_preflight(text)
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

    assert "PACKAGE_VERSION: 0.2.1" in text
    assert "PACKAGE_FILE: FS.GG.Coordination.Cli.0.2.1.nupkg" in text
    assert "PORTABLE_BUNDLE: portable-workspace-v1-0.2.1.zip" in text
    assert "PORTABLE_IMAGE: portable-workspace-linux-amd64-0.2.1.oci.tar" in text
    assert "0.2.0" not in text

    cleanup = text.index("- name: Remove exact private runtime state")
    assert cleanup > first_effect
    assert 'rm -rf -- "$PUBLISH_ROOT" "$PUBLISH_RUNROOT" "$PUBLISH_STATE"' in text
    assert 'test ! -e "$PUBLISH_ROOT" && test ! -e "$PUBLISH_RUNROOT" && test ! -e "$PUBLISH_STATE"' in text


if __name__ == "__main__":
    main()
