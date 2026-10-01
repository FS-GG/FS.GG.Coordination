#!/usr/bin/env python3
"""Check provider phase ordering and the concrete join/authorization validators."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github/workflows/portable-workspace-python-provider-qualification.yml"
VALIDATOR_PATH = Path(__file__).with_name("validate_provider_input.py")
SPEC = importlib.util.spec_from_file_location("provider_input", VALIDATOR_PATH)
assert SPEC and SPEC.loader
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path: Path, value: bytes = b"candidate") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(value)


def main() -> None:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    assert workflow.index("source-contract:") < workflow.index("candidate-facts:")
    assert workflow.count("needs: source-contract") == 1
    assert "inputs.phase == 'candidate-facts'" in workflow
    assert "authorized-qualification" not in workflow
    for forbidden in ("native_admission", "environment:", "sudo ", "useradd", "podman ",
                      "/etc/fsgg/portable-workspaces", " load --input", "qualify invoke"):
        assert forbidden not in workflow
    assert "run-id: ${{ inputs.upstream_run_id }}" in workflow
    assert "provider_runtime.py artifact-custody" in workflow
    assert workflow.index("cleanup-paths") < workflow.index("Upload bounded unauthorized public facts after cleanup")
    assert "nativeExecutionAuthorized" in workflow
    assert "required-external" in workflow
    assert "gh release" not in workflow
    assert "nuget push" not in workflow
    assert "docker push" not in workflow

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
