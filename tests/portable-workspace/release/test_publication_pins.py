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
    assert "if: inputs.preflight_only == true && !inputs.cli_only" in preflight
    assert "  publish:\n    if: inputs.preflight_only != true && !inputs.cli_only" in text
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
                return Response([{"name": "0.3.0" if mode == "org-collision" else "0.2.0"}])
            assert url == "https://api.nuget.org/v3-flatcontainer/fs.gg.coordination.cli/index.json"
            return Response({"versions": ["0.3.0" if mode == "public-collision" else "0.2.0"]})
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



def verify_successor(text: str) -> None:
    import json, os, tempfile
    from unittest.mock import patch
    job = text.split("\n  cli_successor:",1)[1]
    assert "inputs.cli_only && !inputs.preflight_only" in job
    assert "dotnet pack" not in job and "portable-workspace-release" not in job
    assert job.count('dotnet nuget push "$CANDIDATE_OUTPUT/$PACKAGE_FILE"') == 2
    require_in_order(job,["Bind disabled-by-default", "Bind A ancestry", "Download and verify", "Verify provenance", "Observe both feeds", "Recheck current publisher B", "Push original A package to GitHub", "Read and verify GitHub", "Push those same original A bytes", "verify repository signature", "write A/B readback", "only after both feeds"])
    assert '--publisher "$GITHUB_SHA"' in job
    assert 'test "$REQUESTED_PUBLISHER" = "$GITHUB_SHA"' in job
    assert 'timeout 30s dotnet nuget verify' in job and 'head -c 65537' in job
    assert 'git diff --name-only "$SOURCE_A" "$GITHUB_SHA"' in job
    blocks = re.findall(r"python3 - <<'PY'\n(.*?)          PY\n",job,re.S)
    assert len(blocks)==2
    code = "\n".join(line[10:] for line in blocks[0].splitlines())
    actual = json.loads((ROOT/'eng/callable-cli-successor-admission.json').read_text())
    assert actual == {'schema': 'fsgg.coordination.cli-successor-admission/1', 'enabled': True, 'operation': 'publish-main-cli-030-20261009', 'version': '0.3.0', 'source': '8eef1ab7f205132553632e03dc650e7ecdd03679', 'tree': '583c70b4643b5c488d7410ef576806baf0ff8df3', 'protectedMerge': '8eef1ab7f205132553632e03dc650e7ecdd03679', 'runId': 37977490696, 'artifactId': 11638674063, 'archiveSha256': 'cdaef32fb1e14691e8a9d72fedde06fe9d199fed1ed074da5e7954cf422a8653', 'packageSha256': 'a8cd6d602e1203257e1241df0b5dfdb9d867334b46dc406d8cdaa8e6d2b3019c', 'manifestSha256': '86468a295579982859b2a38d13d55c0da88b6d694f6e2bd01157287d9794235f'}
    good = dict(actual,enabled=True,operation='publish-main-cli-030-fixture',source='a'*40,tree='b'*40,protectedMerge='c'*40,runId=1,artifactId=2,archiveSha256='d'*64,packageSha256='e'*64,manifestSha256='f'*64)
    env=dict(OPERATION=good['operation'],REQUESTED_SOURCE=good['source'],REQUESTED_MERGE=good['protectedMerge'],REQUESTED_SHA256=good['packageSha256'],REQUESTED_PUBLISHER='1'*40)
    cases=[actual,good,dict(good,enabled=False),dict(good,runId=True),dict(good,source='0'*40),dict(good,packageSha256='wrong'),dict(good,operation='publish-v2-diag-01-4-cli-021'),dict(good,extra=True)]
    with tempfile.TemporaryDirectory() as directory:
        output=Path(directory)/'env'
        for index,value in enumerate(cases):
            output.unlink(missing_ok=True)
            from io import StringIO
            original_open=open
            def fake_open(path,*args,**kwargs):
                if path=='eng/callable-cli-successor-admission.json':return StringIO(json.dumps(value))
                return original_open(path,*args,**kwargs)
            with patch.dict(os.environ,{**env,'GITHUB_ENV':str(output)},clear=False),patch('builtins.open',fake_open):
                failed=False
                try:exec(compile(code,'actual-successor-admission','exec'),{})
                except AssertionError:failed=True
            assert failed == (index != 1)
            assert output.exists() == (index == 1)

    import importlib.util, subprocess
    spec=importlib.util.spec_from_file_location('installed_harness',ROOT/'eng/test-callable-cli-installed-harness.py')
    helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
    assert job.count('--execute-main-refusal')==2
    with tempfile.TemporaryDirectory() as directory:
        root=Path(directory);tool=root/'tool';tool.write_text('inert');output=root/'receipt'
        expected=b'ordinary-settlement-refused:SettlementProviderUnavailable "missing-environment:FSGG_V2_PREFLIGHT_RECEIPT"\n'
        cases=[(3,b'',expected),(0,b'',expected),(3,b'output',expected),(3,b'',b'unknown-command\n')]
        for index,(status,stdout,stderr) in enumerate(cases):
            output.unlink(missing_ok=True)
            def fake_run(argv,**kwargs):
                assert argv==[str(tool),'ordinary-settlement','execute-main']
                assert kwargs['timeout']==15 and kwargs['capture_output']
                assert not any(k.startswith(('FSGG_V2_','V2_ORDINARY_','GITHUB_')) or k=='GH_TOKEN' for k in kwargs['env'])
                return subprocess.CompletedProcess(argv,status,stdout,stderr)
            with patch.dict(os.environ,{'FSGG_V2_PREFLIGHT_RECEIPT':'polluted','GH_TOKEN':'fixture-secret'}),patch.object(helper.subprocess,'run',fake_run):
                failed=False
                try:helper.check_execute_main_refusal(tool,output)
                except RuntimeError:failed=True
            assert failed==(index!=0) and output.exists()==(index==0)

    # Execute the original archive consumption block with bounded inert bytes.
    import hashlib, zipfile
    archive_code="\n".join(line[10:] for line in blocks[1].splitlines())
    with tempfile.TemporaryDirectory() as directory:
        base=Path(directory)
        manifest=dict(schema='fsgg.coordination.callable-cli-release-preparation/1',packageId='FS.GG.Coordination.Cli',version='0.3.0',sourceCommit='a'*40,sourceTree='b'*40,packageSha256=hashlib.sha256(b'package').hexdigest(),publicationAuthorized=False,tagAuthorized=False)
        manifest_bytes=json.dumps(manifest).encode()
        env={'READBACK_OUTPUT':str(base),'PACKAGE_FILE':'FS.GG.Coordination.Cli.0.3.0.nupkg','PACKAGE_ID':'FS.GG.Coordination.Cli','PACKAGE_SHA':manifest['packageSha256'],'MANIFEST_SHA':hashlib.sha256(manifest_bytes).hexdigest(),'SOURCE_A':'a'*40,'TREE_A':'b'*40}
        for case in ('good','extra','wrong-package','wrong-manifest','wrong-source'):
            root=base/case;root.mkdir()
            with zipfile.ZipFile(base/'candidate.zip','w') as z:
                z.writestr(env['PACKAGE_FILE'],b'wrong' if case=='wrong-package' else b'package')
                z.writestr('callable-cli-release-manifest.json',b'wrong' if case=='wrong-manifest' else manifest_bytes)
                if case=='extra':z.writestr('../unexpected',b'bad')
            change={'SOURCE_A':'c'*40} if case=='wrong-source' else {}
            with patch.dict(os.environ,{**env,**change,'CANDIDATE_OUTPUT':str(root)},clear=False):
                failed=False
                try:exec(compile(archive_code,'actual-candidate-consumption','exec'),{})
                except AssertionError:failed=True
            assert failed == (case!='good')
            assert not (base/'unexpected').exists()
    # Exercise the actual bounded verifier call, including fault/timeout/output refusal.
    import subprocess
    signature=job.split('          if test "$signed" = true; then\n',1)[1].split('          fi\n',1)[0]
    signature='if test "$signed" = true; then\n'+'\n'.join(line[10:] for line in signature.splitlines())+'\nfi\n'
    with tempfile.TemporaryDirectory() as directory:
        root=Path(directory)
        (root/'dotnet').write_text('#!/bin/sh\necho called >> "$READBACK_OUTPUT/calls"\ncase "$MODE" in failure) exit 1;; oversize) head -c 70000 /dev/zero;; *) echo verified;; esac\n')
        (root/'timeout').write_text('#!/bin/sh\ntest "$MODE" != timeout || exit 124\nshift; exec "$@"\n')
        for name in ('dotnet','timeout'):(root/name).chmod(0o700)
        for mode in ('success','failure','timeout','oversize','unsigned'):
            (root/'calls').unlink(missing_ok=True)
            env=dict(os.environ,PATH=str(root)+os.pathsep+os.environ['PATH'],MODE=mode,READBACK_OUTPUT=str(root),signed='false' if mode=='unsigned' else 'true')
            run=subprocess.run(['bash','-c','set -euo pipefail\n'+signature],env=env,capture_output=True,timeout=5)
            assert (run.returncode==0)==(mode in ('success','unsigned')),mode
            assert (root/'calls').exists()==(mode not in ('timeout','unsigned'))
            log=root/'signature-verification.txt'
            if log.exists():assert log.stat().st_size<=65537


def main() -> None:
    text = WORKFLOW.read_text(encoding="utf-8")
    verify_successor(text)
    text = text.split("\n  cli_successor:", 1)[0]
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
