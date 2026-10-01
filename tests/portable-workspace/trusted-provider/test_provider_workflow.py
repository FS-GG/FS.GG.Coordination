#!/usr/bin/env python3
"""Check provider phase ordering and the concrete join/authorization validators."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github/workflows/portable-workspace-python-provider-qualification.yml"
VALIDATOR_PATH = Path(__file__).with_name("validate_provider_input.py")
SPEC = importlib.util.spec_from_file_location("provider_input", VALIDATOR_PATH)
assert SPEC and SPEC.loader
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)
FACTS_SPEC = importlib.util.spec_from_file_location("provider_facts", Path(__file__).with_name("provider_facts.py"))
assert FACTS_SPEC and FACTS_SPEC.loader
FACTS = importlib.util.module_from_spec(FACTS_SPEC); FACTS_SPEC.loader.exec_module(FACTS)
STAGE_SPEC = importlib.util.spec_from_file_location("stage_provider_input", Path(__file__).with_name("stage_provider_input.py"))
assert STAGE_SPEC and STAGE_SPEC.loader
STAGE = importlib.util.module_from_spec(STAGE_SPEC); STAGE_SPEC.loader.exec_module(STAGE)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path: Path, value: bytes = b"candidate") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(value)


def workflow_shell(workflow: str, name: str) -> str:
    marker = f"      - name: {name}\n"
    section = workflow.split(marker, 1)[1]
    lines = section.splitlines()
    start = next(index for index, line in enumerate(lines) if line == "        run: |") + 1
    body: list[str] = []
    for line in lines[start:]:
        if line.startswith("      - "):
            break
        if line and not line.startswith("          "):
            break
        body.append(line[10:] if line else "")
    return "\n".join(body) + "\n"


def run_shell(script: str, environment: dict[str, str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["bash", "-c", script], cwd=ROOT, env={**os.environ, **environment},
                          text=True, capture_output=True, check=False)


def main() -> None:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    assert workflow.index("source-contract:") < workflow.index("stage-inputs:") < workflow.index("candidate-facts:")
    assert workflow.count("needs: source-contract") == 2
    assert "inputs.phase == 'stage-inputs'" in workflow
    assert "inputs.phase == 'candidate-facts'" in workflow
    assert "authorized-qualification" not in workflow
    for forbidden in ("native_admission", "environment:", "sudo ", "useradd", "podman ",
                      "/etc/fsgg/portable-workspaces", " load --input", "qualify invoke"):
        assert forbidden not in workflow
    assert "run-id: ${{ inputs.upstream_run_id }}" in workflow
    assert "provider_runtime.py artifact-custody" in workflow
    assert "stage_provider_input.py" in workflow
    assert "STAGE_ROOT_OWNED: ${{ steps.stage-reservation.outputs.stage_root_owned }}" in workflow
    assert "test_provider_facts_runtime_bounds.py" in workflow
    assert "dotnet fsi --exec tests/portable-workspace/trusted-provider/test_private_input_manifest.fsx" in workflow
    staging_helper = (ROOT / "tests/portable-workspace/trusted-provider/stage_provider_input.py").read_text(encoding="utf-8")
    assert "callable-cli-release-prepare.yml" in staging_helper
    assert "fsgg.portable-python-provider-staging-provenance/1" in staging_helper
    assert "portable-python-provider-input-${{ github.sha }}" in workflow
    assert "FS.GG.Coordination.Cli.0.2.0.nupkg" not in workflow
    assert "dotnet-runtime-10.0.12-linux-x64.tar.gz" in workflow
    assert "8458f4cef855fcebd139d9853e47fb0a5d86ab65d4aa101ea158a11e036c0fa4" not in workflow  # assembler owns the checksum
    assert workflow.index("cleanup-paths") < workflow.index("Upload bounded unauthorized public facts after cleanup")
    assert "nativeExecutionAuthorized" in workflow
    assert "required-external" in workflow
    assert "gh release" not in workflow
    assert "nuget push" not in workflow
    assert "docker push" not in workflow
    policy = (ROOT / "src/FS.GG.Coordination.Cli/PortableWorkspacePythonHelloPolicy.fs").read_text()
    assert FACTS.IMAGE == STAGE.IMAGE_REFERENCE and FACTS.IMAGE in policy
    assert STAGE.IMAGE_CONFIG_DIGEST.removeprefix("sha256:") in policy

    reservation = workflow_shell(workflow, "Reserve exclusive public staging root")
    cleanup = workflow_shell(workflow, "Remove downloaded producer archives")
    with tempfile.TemporaryDirectory(prefix="provider-stage-workflow-") as temporary:
        temporary_root = Path(temporary)
        source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=ROOT, text=True).strip()
        common = {
            "P_ARTIFACT": "1", "P_RUN": "2", "P_ATTEMPT": "1", "P_SOURCE": source, "P_TREE": tree,
            "P_DIGEST": "sha256:" + "1" * 64, "GITHUB_RUN_ID": "991001", "GITHUB_RUN_ATTEMPT": "1",
        }
        owned_root = Path("/tmp/p4-public-stage-991001-1")
        owned_output = Path("/tmp/p4-public-stage-output-991001-1")
        for path in (owned_root, owned_output):
            assert not path.exists()
        output = temporary_root / "github-output"
        environment = {**common, "STAGE_ROOT": str(owned_root), "STAGE_OUTPUT": str(owned_output), "GITHUB_OUTPUT": str(output)}
        result = run_shell(reservation, environment); assert result.returncode == 0, result.stderr
        assert output.read_text() == "stage_root_owned=true\n" and owned_root.stat().st_mode & 0o777 == 0o700
        write(owned_root / "download.zip")
        result = run_shell(cleanup, {**environment, "STAGE_ROOT_OWNED": "true"}); assert result.returncode == 0, result.stderr
        assert not owned_root.exists()

        collision_root = Path("/tmp/p4-public-stage-991002-1"); collision_output = Path("/tmp/p4-public-stage-output-991002-1")
        collision_root.mkdir(mode=0o700); sentinel = collision_root / "sentinel"; write(sentinel, b"preexisting")
        collision_marker = temporary_root / "collision-output"
        collision = {**common, "GITHUB_RUN_ID": "991002", "STAGE_ROOT": str(collision_root),
                     "STAGE_OUTPUT": str(collision_output), "GITHUB_OUTPUT": str(collision_marker)}
        result = run_shell(reservation, collision); assert result.returncode != 0
        result = run_shell(cleanup, {**collision, "STAGE_ROOT_OWNED": ""}); assert result.returncode == 0, result.stderr
        assert sentinel.read_bytes() == b"preexisting"
        sentinel.unlink(); collision_root.rmdir()

        blocked_root = Path("/tmp/p4-public-stage-991003-1"); blocked_output = Path("/tmp/p4-public-stage-output-991003-1")
        blocked_output.mkdir(mode=0o700); blocked_sentinel = blocked_output / "sentinel"; write(blocked_sentinel, b"output")
        blocked_marker = temporary_root / "blocked-output"
        blocked = {**common, "GITHUB_RUN_ID": "991003", "STAGE_ROOT": str(blocked_root),
                   "STAGE_OUTPUT": str(blocked_output), "GITHUB_OUTPUT": str(blocked_marker)}
        result = run_shell(reservation, blocked); assert result.returncode != 0 and not blocked_root.exists()
        result = run_shell(cleanup, {**blocked, "STAGE_ROOT_OWNED": ""}); assert result.returncode == 0, result.stderr
        assert blocked_sentinel.read_bytes() == b"output"
        blocked_sentinel.unlink(); blocked_output.rmdir()

        raced_root = Path("/tmp/p4-public-stage-991004-1"); raced_output = Path("/tmp/p4-public-stage-output-991004-1")
        fake_bin = temporary_root / "race-bin"; fake_bin.mkdir()
        fake_mkdir = fake_bin / "mkdir"
        fake_mkdir.write_text("""#!/usr/bin/env bash
set -euo pipefail
target="${@: -1}"
/usr/bin/mkdir -m 0700 -- "$target"
printf unowned > "$target/sentinel"
exec /usr/bin/mkdir "$@"
""")
        fake_mkdir.chmod(0o700)
        raced_marker = temporary_root / "raced-output"; raced_marker.write_text("")
        raced = {**common, "GITHUB_RUN_ID": "991004", "STAGE_ROOT": str(raced_root),
                 "STAGE_OUTPUT": str(raced_output), "GITHUB_OUTPUT": str(raced_marker),
                 "PATH": str(fake_bin) + os.pathsep + os.environ["PATH"]}
        result = run_shell(reservation, raced); assert result.returncode != 0
        assert raced_marker.read_text() == "" and (raced_root / "sentinel").read_bytes() == b"unowned"
        result = run_shell(cleanup, {**raced, "STAGE_ROOT_OWNED": ""}); assert result.returncode == 0, result.stderr
        assert (raced_root / "sentinel").read_bytes() == b"unowned"
        (raced_root / "sentinel").unlink(); raced_root.rmdir()

    with tempfile.TemporaryDirectory(prefix="provider-input-") as temporary:
        root = Path(temporary)
        paths = {
            "coordination/package.nupkg": b"coordination",
            "sdd/package.nupkg": b"sdd",
            "templates/package.nupkg": b"templates",
            "templates/python.providers.yml": b"source: FS.GG.Workspace.Template::<pin>\n",
            "runtime/runtime.tar": b"runtime",
            "image/image.oci.tar": b"image",
            "image/manifest.json": b"manifest",
        }
        for name, value in paths.items():
            write(root / name, value)
        join = {
            "schema": "fsgg.portable-workspace-python-provider-input/1",
            "coordination": {
                "package": "coordination/package.nupkg",
                "sha256": digest(root / "coordination/package.nupkg"),
                "version": "0.2.1",
                "sourceRevision": "a" * 40,
                "sourceTree": "b" * 40,
            },
            "sdd": {
                "package": "sdd/package.nupkg",
                "sha256": digest(root / "sdd/package.nupkg"),
                "version": "1.2.3",
            },
            "templates": {
                "package": "templates/package.nupkg",
                "sha256": digest(root / "templates/package.nupkg"),
                "version": "0.15.1",
                "descriptor": "templates/python.providers.yml",
                "descriptorSha256": digest(root / "templates/python.providers.yml"),
            },
            "runtime": {
                "archive": "runtime/runtime.tar",
                "sha256": digest(root / "runtime/runtime.tar"),
                "version": "10.0.12",
            },
            "image": {
                "archive": "image/image.oci.tar",
                "archiveSha256": digest(root / "image/image.oci.tar"),
                "manifest": "image/manifest.json",
                "manifestSha256": digest(root / "image/manifest.json"),
            },
        }
        (root / "provider-input.json").write_text(json.dumps(join))
        output = root / "validated.json"
        VALIDATOR.validate_join(root, output)
        assert json.loads(output.read_text()) == join
        (root / "image/manifest.json").write_bytes(b"changed")
        try:
            VALIDATOR.validate_join(root, output)
            raise AssertionError("changed joined artifact was accepted")
        except ValueError as error:
            assert "digest changed" in str(error)

    with tempfile.TemporaryDirectory(prefix="provider-authorization-") as temporary:
        root = Path(temporary)
        facts_root, auth_root = root / "facts", root / "auth"
        upstream = root / "upstream"
        for name, value in {
            "coordination/package.nupkg": b"coordination", "sdd/package.nupkg": b"sdd",
            "templates/package.nupkg": b"templates", "templates/python.providers.yml": b"descriptor",
            "runtime/runtime.tar": b"runtime", "image/image.oci.tar": b"image", "image/manifest.json": b"manifest",
        }.items(): write(upstream / name, value)
        joined = dict(join)
        joined = json.loads(json.dumps(joined))
        for section, path_key, digest_key in [
            ("coordination", "package", "sha256"), ("sdd", "package", "sha256"),
            ("templates", "package", "sha256"), ("templates", "descriptor", "descriptorSha256"),
            ("runtime", "archive", "sha256"), ("image", "archive", "archiveSha256"),
            ("image", "manifest", "manifestSha256"),
        ]: joined[section][digest_key] = digest(upstream / joined[section][path_key])
        join_path = facts_root / "validated-join.json"
        write(upstream / "provider-input.json", (json.dumps(joined) + "\n").encode())
        write(join_path, (json.dumps(joined, sort_keys=True, separators=(",", ":")) + "\n").encode())
        receiver_archive, profile_path, candidate_path = facts_root / "receiver.tar", facts_root / "profile.json", facts_root / "candidate-receipt.json"
        write(receiver_archive, b"receiver"); write(profile_path, b"profile"); write(candidate_path, b"candidate")
        facts = {
            "schema": "fsgg.portable-workspace-python-provider-facts/1",
            "authorizationState": "required-external",
            "nativeExecutionAuthorized": False,
            "joinSha256": digest(join_path),
            "receiverArchiveSha256": digest(receiver_archive),
            "candidateReceiptSha256": digest(candidate_path),
            "allowedUid": 32001,
            "producerSourceRevision": "a" * 40,
            "package": {"version": "0.2.1", "sha256": "1" * 64},
            "installedCli": {"payloadSha256": "2" * 64},
            "provider": {
                "templateVersion": "0.15.1",
                "templatePackageSha256": "3" * 64,
            },
            "receiver": {
                "commit": "b" * 40,
                "tree": "c" * 40,
                "projectedPayload": [{"path": "python/app.py", "sha256": "4" * 64}],
            },
            "profileSha256": digest(profile_path),
            "executables": {
                "git": {"path": "/usr/bin/git", "sha256": "6" * 64},
                "tar": {"path": "/usr/bin/tar", "sha256": "7" * 64},
                "podman": {"path": "/usr/bin/podman", "sha256": "8" * 64},
            },
            "image": {
                "qualifiedImage": "localhost/image@sha256:" + "9" * 64,
                "archiveSha256": "a" * 64,
                "manifestDigest": "b" * 64,
                "configDigest": "c" * 64,
                "manifestReceiptSha256": "d" * 64,
            },
        }
        write(facts_root / "provider-facts.json", (json.dumps(facts) + "\n").encode())
        grant = {
            "schema": "fsgg.portable-workspace-python-enrollment/v1",
            "enrollmentId": "local-python-hello-v1",
            "grantId": "p4-python-provider-test",
            "allowedUid": 32001,
            "cli": {
                "version": "0.2.1",
                "packageSha256": "1" * 64,
                "payloadSha256": "2" * 64,
            },
            "provider": {
                "version": "0.15.1",
                "packageSha256": "3" * 64,
                "producerSourceRevision": "a" * 40,
            },
            "receiver": {
                "repositoryPath": "/srv/p4-receiver",
                "commit": "b" * 40,
                "tree": "c" * 40,
                "projectedPayload": [{"path": "python/app.py", "sha256": "4" * 64}],
            },
            "profileSha256": digest(profile_path),
            "workspaceScope": "fs-gg/p4-python-receiver",
            "workflowRevision": 1,
            "fenceGeneration": 1,
            "observedAt": "2026-10-01T00:00:00.000000Z",
            "stateRoot": "/p4",
            "executables": facts["executables"],
            "image": {
                "qualifiedImage": "localhost/image@sha256:" + "9" * 64,
                "archiveSha256": "a" * 64,
                "manifestDigest": "b" * 64,
                "configDigest": "c" * 64,
                "recipeSha256": "d" * 64,
            },
        }
        write(auth_root / "python-hello-v1.json", (json.dumps(grant) + "\n").encode())
        authority = {
            "schema": "fsgg.portable-workspace-python-provider-authorization/1",
            "providerFactsSha256": digest(facts_root / "provider-facts.json"),
            "grantSha256": digest(auth_root / "python-hello-v1.json"),
            "authorizedBy": "external-provider",
            "authorizedAt": "2026-10-01T00:00:00.000000Z",
            "nativeExecutionAuthorized": True,
        }
        write(auth_root / "authorization.json", (json.dumps(authority) + "\n").encode())
        VALIDATOR.validate_authorization(
            facts_root, auth_root, upstream, join_path, receiver_archive, profile_path, candidate_path,
            "f" * 40, "e" * 40, root / "validated-facts.json", root / "validated-grant.json"
        )
        changed_facts = json.loads((facts_root / "provider-facts.json").read_text())
        changed_facts["image"]["archiveSha256"] = "e" * 64
        write(facts_root / "provider-facts.json", (json.dumps(changed_facts) + "\n").encode())
        changed_authority = dict(authority); changed_authority["providerFactsSha256"] = digest(facts_root / "provider-facts.json")
        write(auth_root / "authorization.json", (json.dumps(changed_authority) + "\n").encode())
        try:
            VALIDATOR.validate_authorization(
                facts_root, auth_root, upstream, join_path, receiver_archive, profile_path, candidate_path,
                "f" * 40, "e" * 40, root / "changed-archive-facts.json", root / "changed-archive-grant.json"
            )
            raise AssertionError("changed image archive was accepted without matching grant admission")
        except ValueError as error:
            assert "exact image custody facts" in str(error)
        write(facts_root / "provider-facts.json", (json.dumps(facts) + "\n").encode())
        changed_grant = json.loads((auth_root / "python-hello-v1.json").read_text())
        changed_grant["image"]["recipeSha256"] = "e" * 64
        write(auth_root / "python-hello-v1.json", (json.dumps(changed_grant) + "\n").encode())
        changed_authority = dict(authority); changed_authority["grantSha256"] = digest(auth_root / "python-hello-v1.json")
        write(auth_root / "authorization.json", (json.dumps(changed_authority) + "\n").encode())
        try:
            VALIDATOR.validate_authorization(
                facts_root, auth_root, upstream, join_path, receiver_archive, profile_path, candidate_path,
                "f" * 40, "e" * 40, root / "changed-receipt-facts.json", root / "changed-receipt-grant.json"
            )
            raise AssertionError("changed image receipt was accepted without matching facts admission")
        except ValueError as error:
            assert "exact image custody facts" in str(error)
        write(auth_root / "python-hello-v1.json", (json.dumps(grant) + "\n").encode())
        write(auth_root / "authorization.json", (json.dumps(authority) + "\n").encode())
        receiver_archive.write_bytes(b"changed receiver")
        try:
            VALIDATOR.validate_authorization(
                facts_root, auth_root, upstream, join_path, receiver_archive, profile_path, candidate_path,
                "f" * 40, "e" * 40, root / "changed-receiver-facts.json", root / "changed-receiver-grant.json"
            )
            raise AssertionError("changed receiver archive was accepted")
        except ValueError as error:
            assert "do not bind" in str(error)
        receiver_archive.write_bytes(b"receiver")
        changed_join = json.loads((upstream / "provider-input.json").read_text())
        changed_join["coordination"]["sourceRevision"] = "9" * 40
        (upstream / "provider-input.json").write_text(json.dumps(changed_join))
        try:
            VALIDATOR.validate_authorization(
                facts_root, auth_root, upstream, join_path, receiver_archive, profile_path, candidate_path,
                "f" * 40, "e" * 40, root / "changed-join-facts.json", root / "changed-join-grant.json"
            )
            raise AssertionError("changed upstream join was accepted")
        except ValueError as error:
            assert "differs" in str(error)
        (upstream / "provider-input.json").write_text(json.dumps(joined))
        authority["providerFactsSha256"] = "0" * 64
        write(auth_root / "authorization.json", (json.dumps(authority) + "\n").encode())
        try:
            VALIDATOR.validate_authorization(
                facts_root, auth_root, upstream, join_path, receiver_archive, profile_path, candidate_path,
                "f" * 40, "e" * 40, root / "bad-facts.json", root / "bad-grant.json"
            )
            raise AssertionError("authorization for different facts was accepted")
        except ValueError as error:
            assert "does not bind" in str(error)

    with tempfile.TemporaryDirectory(prefix="provider-runtime-") as temporary:
        root = Path(temporary)
        archive = root / "runtime.tar"
        with tarfile.open(archive, "w") as value:
            info = tarfile.TarInfo("../escape")
            payload = b"bad"
            info.size = len(payload)
            value.addfile(info, io.BytesIO(payload))
        try:
            VALIDATOR.extract_runtime(archive, root / "runtime")
            raise AssertionError("runtime traversal was accepted")
        except ValueError as error:
            assert "unsafe entry" in str(error)
        receiver = root / "receiver.tar"
        with tarfile.open(receiver, "w") as value:
            info = tarfile.TarInfo("p4-receiver/link")
            info.type = tarfile.SYMTYPE
            info.linkname = "/etc/passwd"
            value.addfile(info)
        try:
            VALIDATOR.extract_receiver(receiver, root / "receiver")
            raise AssertionError("linked receiver entry was accepted")
        except ValueError as error:
            assert "unsafe entry" in str(error)


if __name__ == "__main__":
    main()
