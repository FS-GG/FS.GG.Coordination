#!/usr/bin/env python3
"""Behavioral tests for genuine public P4 input staging."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import tarfile
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("stage_provider_input", HERE / "stage_provider_input.py")
assert SPEC and SPEC.loader
STAGE = importlib.util.module_from_spec(SPEC); SPEC.loader.exec_module(STAGE)
VALIDATOR_SPEC = importlib.util.spec_from_file_location("validate_provider_input", HERE / "validate_provider_input.py")
assert VALIDATOR_SPEC and VALIDATOR_SPEC.loader
VALIDATOR = importlib.util.module_from_spec(VALIDATOR_SPEC); VALIDATOR_SPEC.loader.exec_module(VALIDATOR)


def sha(value: bytes) -> str: return hashlib.sha256(value).hexdigest()


def package(path: Path, package_id: str, version: str) -> None:
    nuspec = f"<package><metadata><id>{package_id}</id><version>{version}</version></metadata></package>"
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(f"{package_id}.nuspec", nuspec); archive.writestr("payload.bin", b"payload")


def artifact(path: Path, files: dict[str, Path | bytes]) -> None:
    with zipfile.ZipFile(path, "w") as archive:
        for name, value in files.items(): archive.writestr(name, value.read_bytes() if isinstance(value, Path) else value)


def run_record(expected: dict[str, object]) -> dict[str, object]:
    return {"id": expected["runId"], "run_attempt": expected["runAttempt"], "head_sha": expected["headSha"],
            "path": expected["workflow"], "event": expected["event"], "conclusion": "success",
            "repository": {"full_name": expected["repository"]}}


def artifact_record(expected: dict[str, object]) -> dict[str, object]:
    return {"id": expected["artifactId"], "name": expected["artifactName"], "digest": expected["artifactDigest"],
            "expired": False, "workflow_run": {"id": expected["runId"], "head_sha": expected["headSha"]}}


def write_json(path: Path, value: object) -> None: path.write_text(json.dumps(value), encoding="utf-8")


def fixture(root: Path) -> argparse.Namespace:
    source, tree, staging, staging_tree = "a" * 40, "b" * 40, "c" * 40, "d" * 40
    p_package = root / "coord.nupkg"; package(p_package, "FS.GG.Coordination.Cli", "0.2.1")
    image = root / "candidate.oci.tar"; image.write_bytes(b"oci")
    image_manifest = {
        "schema": "fsgg.portable-workspace-local-image/1", "source": {"revision": source, "tree": tree},
        "image": {"name": STAGE.IMAGE_NAME, "digest": STAGE.IMAGE_DIGEST,
                  "archiveConfigDigest": STAGE.IMAGE_CONFIG_DIGEST, "reference": STAGE.IMAGE_REFERENCE},
    }
    callable_manifest = {"schema": "fsgg.coordination.callable-cli-release-preparation/1", "version": "0.2.1",
        "sourceCommit": source, "sourceTree": tree, "packageSha256": STAGE.digest(p_package),
        "publicationAuthorized": False, "tagAuthorized": False}
    image_manifest_bytes = STAGE.canonical(image_manifest)
    release = {"schema": "fsgg.portable-workspace-release/1", "version": "0.2.1", "sourceCommit": source,
        "sourceTree": tree, "packageName": "FS.GG.Coordination.Cli.0.2.1.nupkg", "packageSha256": STAGE.digest(p_package),
        "imageArchiveName": "portable-workspace-linux-amd64-0.2.1.oci.tar", "imageArchiveSha256": STAGE.digest(image),
        "imageManifestSha256": sha(image_manifest_bytes), "imageDigest": STAGE.IMAGE_DIGEST,
        "imageReference": STAGE.IMAGE_REFERENCE, "publicationAuthorized": False,
        "tagAuthorized": False, "activationAuthorized": False}
    packaged = {"schema": "fsgg.portable-workspace-packaged-qualification/1", "packageSha256": STAGE.digest(p_package),
        "qualificationExitCode": 0, "publicationAuthorized": False, "activationAuthorized": False}
    cleanup = {"schema": "fsgg.portable-workspace-release-cleanup/1", "allRemoved": True}
    prep_zip = root / "prep.zip"
    artifact(prep_zip, {"FS.GG.Coordination.Cli.0.2.1.nupkg": p_package,
        "portable-workspace-linux-amd64-0.2.1.oci.tar": image, "portable-workspace-image-manifest.json": image_manifest_bytes,
        "callable-cli-release-manifest.json": STAGE.canonical(callable_manifest),
        "portable-workspace-release-manifest.json": STAGE.canonical(release),
        "portable-workspace-packaged-qualification.json": STAGE.canonical(packaged), "evidence/cleanup-state.json": STAGE.canonical(cleanup)})
    sdd_package = root / "sdd.nupkg"; package(sdd_package, "FS.GG.SDD.Cli", "2.1.0")
    templates_package = root / "templates.nupkg"; package(templates_package, "FS.GG.Workspace.Template", "0.18.0")
    for ident, expected in ((31, STAGE.SDD), (41, STAGE.TEMPLATES)):
        expected.update(runId=ident, runAttempt=1, artifactId=ident+1,
            workflow=".github/workflows/release.yml", headSha="e"*40,
            artifactName=f"synthetic-{ident}", event="workflow_dispatch")
    STAGE.SDD["packageSha256"] = STAGE.digest(sdd_package); STAGE.TEMPLATES["packageSha256"] = STAGE.digest(templates_package)
    sdd_zip = root / "sdd.zip"; artifact(sdd_zip, {STAGE.SDD["package"]: sdd_package})
    templates_zip = root / "templates.zip"; artifact(templates_zip, {STAGE.TEMPLATES["package"]: templates_package})
    STAGE.SDD["artifactDigest"] = "sha256:" + STAGE.digest(sdd_zip)
    STAGE.TEMPLATES["artifactDigest"] = "sha256:" + STAGE.digest(templates_zip)
    runtime = root / "runtime.tar.gz"; runtime.write_bytes(b"runtime")
    STAGE.RUNTIME_SHA256 = STAGE.digest(runtime); STAGE.RUNTIME_SHA512 = STAGE.digest(runtime, "sha512")
    descriptor = root / "descriptor.yml"; descriptor.write_text("    contractVersion: \"2.0.0\"\n    minimumFsggSdd:\n      version: \"2.1.0\"\n    source: FS.GG.Workspace.Template::0.18.0\n")
    STAGE.DESCRIPTOR["sha256"] = STAGE.digest(descriptor)
    descriptor_commit = root / "descriptor-commit.json"
    write_json(descriptor_commit, {"sha": STAGE.DESCRIPTOR["revision"], "tree": {"sha": STAGE.DESCRIPTOR["tree"]}})
    receiver_custody = root / "receiver-custody.json"
    write_json(receiver_custody, {"schema": "fsgg.portable-current-receiver-custody/1", "sdd": STAGE.SDD, "templates": STAGE.TEMPLATES})
    p_expected = {"repository": "FS-GG/FS.GG.Coordination", "runId": 11, "runAttempt": 1,
        "workflow": ".github/workflows/callable-cli-release-prepare.yml", "headSha": source, "headTree": tree,
        "artifactId": 12, "artifactName": "callable-cli-" + source, "artifactDigest": "sha256:" + STAGE.digest(prep_zip),
        "event": "workflow_dispatch"}
    paths = {}
    for label, expected in (("preparation", p_expected), ("sdd", STAGE.SDD), ("templates", STAGE.TEMPLATES)):
        paths[label + "_run"] = root / (label + "-run.json"); paths[label + "_artifact"] = root / (label + "-artifact.json")
        write_json(paths[label + "_run"], run_record(expected)); write_json(paths[label + "_artifact"], artifact_record(expected))
    return argparse.Namespace(receiver_custody=receiver_custody, preparation_archive=prep_zip, preparation_run=paths["preparation_run"], preparation_artifact=paths["preparation_artifact"],
        sdd_archive=sdd_zip, sdd_run=paths["sdd_run"], sdd_artifact=paths["sdd_artifact"], templates_archive=templates_zip,
        templates_run=paths["templates_run"], templates_artifact=paths["templates_artifact"], runtime=runtime, descriptor=descriptor, descriptor_commit=descriptor_commit,
        output=root / "output", preparation_run_id=11, preparation_run_attempt=1, preparation_artifact_id=12,
        preparation_artifact_digest=p_expected["artifactDigest"], preparation_source=source, preparation_tree=tree,
        staging_source=staging, staging_tree=staging_tree)


def refusal(function, text: str) -> None:
    try: function(); raise AssertionError("invalid staging input accepted")
    except ValueError as error: assert text in str(error)


def rewrite_preparation(args: argparse.Namespace, edit) -> None:
    with zipfile.ZipFile(args.preparation_archive) as archive:
        members = {item.filename: archive.read(item) for item in archive.infolist()}
    edit(members)
    with zipfile.ZipFile(args.preparation_archive, "w") as archive:
        for name, value in members.items(): archive.writestr(name, value)
    changed_digest = "sha256:" + STAGE.digest(args.preparation_archive)
    receipt = json.loads(args.preparation_artifact.read_text())
    receipt["digest"] = changed_digest
    write_json(args.preparation_artifact, receipt)
    args.preparation_artifact_digest = changed_digest


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="p4-stage-") as temporary:
        root = Path(temporary); args = fixture(root); STAGE.assemble(args)
        join = json.loads((args.output / "provider-input.json").read_text())
        provenance = json.loads((args.output / "staging-provenance.json").read_text())
        VALIDATOR.validate_join(args.output, root / "validated-join.json")
        assert (root / "validated-join.json").read_bytes() == (args.output / "provider-input.json").read_bytes()
        assert join["coordination"]["version"] == "0.2.1" and join["coordination"]["sourceRevision"] == "a" * 40
        assert join["image"]["archiveSha256"] == STAGE.digest(args.output / join["image"]["archive"])
        assert provenance["authorizationState"] == "none" and provenance["nativeExecutionAuthorized"] is False
        assert set(path.name for path in args.output.iterdir()) == {"coordination", "sdd", "templates", "runtime", "image", "provider-input.json", "staging-provenance.json"}
        assert not any(word in (args.output / "staging-provenance.json").read_text().lower() for word in ("token", "secret", "grant"))
    with tempfile.TemporaryDirectory(prefix="p4-stage-custody-") as temporary:
        root = Path(temporary); args = fixture(root); value = json.loads(args.preparation_artifact.read_text()); value["digest"] = "sha256:" + "0" * 64; write_json(args.preparation_artifact, value)
        refusal(lambda: STAGE.assemble(args), "artifact custody differs")
    for label in ("preparation_archive", "sdd_archive", "templates_archive"):
        with tempfile.TemporaryDirectory(prefix=f"p4-stage-{label}-digest-") as temporary:
            root = Path(temporary); args = fixture(root)
            with getattr(args, label).open("ab") as archive: archive.write(b"changed archive bytes")
            refusal(lambda: STAGE.assemble(args), "producer archive digest differs")
            assert not args.output.exists()
            assert not any(path.name.startswith(".output-") for path in root.iterdir())
    with tempfile.TemporaryDirectory(prefix="p4-stage-source-") as temporary:
        root = Path(temporary); args = fixture(root)
        with zipfile.ZipFile(args.preparation_archive, "a") as archive: archive.writestr("../escape", b"bad")
        changed_digest = "sha256:" + STAGE.digest(args.preparation_archive)
        value = json.loads(args.preparation_artifact.read_text()); value["digest"] = changed_digest
        write_json(args.preparation_artifact, value); args.preparation_artifact_digest = changed_digest
        refusal(lambda: STAGE.assemble(args), "unsafe or repeated")
        assert not args.output.exists()
        assert not any(path.name.startswith(".output-") for path in root.iterdir())
    with tempfile.TemporaryDirectory(prefix="p4-stage-output-") as temporary:
        root = Path(temporary); args = fixture(root); args.output.mkdir()
        refusal(lambda: STAGE.assemble(args), "output or official runtime custody differs")
    old_digest = "sha256:e76e01afa06a325beb74f7f55c32ed13aebe140c82a37e17c67f2d3217da4c3e"
    def mutate_json(members: dict[str, bytes], name: str, edit) -> None:
        value = json.loads(members[name]); edit(value); members[name] = STAGE.canonical(value)
    with tempfile.TemporaryDirectory(prefix="p4-stage-old-image-") as temporary:
        root = Path(temporary); args = fixture(root)
        def old_image(members):
            mutate_json(members, "portable-workspace-release-manifest.json", lambda value: value.update(imageDigest=old_digest, imageReference=STAGE.IMAGE_NAME + "@" + old_digest))
            mutate_json(members, "portable-workspace-image-manifest.json", lambda value: value["image"].update(digest=old_digest, reference=STAGE.IMAGE_NAME + "@" + old_digest))
        rewrite_preparation(args, old_image)
        refusal(lambda: STAGE.assemble(args), "coherent release provenance differs")
    with tempfile.TemporaryDirectory(prefix="p4-stage-wrong-producer-") as temporary:
        root = Path(temporary); args = fixture(root)
        def wrong_producer(members):
            mutate_json(members, "portable-workspace-image-manifest.json", lambda value: value["source"].update(revision="9" * 40))
            mutate_json(members, "portable-workspace-release-manifest.json", lambda value: value.update(imageManifestSha256=sha(members["portable-workspace-image-manifest.json"])))
        rewrite_preparation(args, wrong_producer)
        refusal(lambda: STAGE.assemble(args), "image source or compiled digest differs")
    with tempfile.TemporaryDirectory(prefix="p4-stage-image-bytes-") as temporary:
        root = Path(temporary); args = fixture(root)
        rewrite_preparation(args, lambda members: members.__setitem__("portable-workspace-linux-amd64-0.2.1.oci.tar", b"changed-image"))
        refusal(lambda: STAGE.assemble(args), "coherent release provenance differs")
    with tempfile.TemporaryDirectory(prefix="p4-stage-receipt-bytes-") as temporary:
        root = Path(temporary); args = fixture(root)
        rewrite_preparation(args, lambda members: mutate_json(members, "portable-workspace-image-manifest.json", lambda value: value.update(unadmitted=True)))
        refusal(lambda: STAGE.assemble(args), "coherent release provenance differs")
    with tempfile.TemporaryDirectory(prefix="p4-stage-false-strict-") as temporary:
        root = Path(temporary); args = fixture(root)
        rewrite_preparation(args, lambda members: mutate_json(members, "portable-workspace-packaged-qualification.json", lambda value: value.update(qualificationExitCode=1)))
        refusal(lambda: STAGE.assemble(args), "packaged qualification differs")
    with tempfile.TemporaryDirectory(prefix="p4-published-tag-push-") as temporary:
        args = fixture(Path(temporary)); value = json.loads(args.receiver_custody.read_text())
        value["templates"]["event"] = "push"
        write_json(args.receiver_custody, value)
        record = json.loads(args.templates_run.read_text()); record["event"] = "push"
        write_json(args.templates_run, record)
        STAGE.assemble(args)
        assert json.loads((args.output / "provider-input.json").read_text())["templates"]["version"] == "0.18.0"
    with tempfile.TemporaryDirectory(prefix="p4-published-tag-push-mismatch-") as temporary:
        args = fixture(Path(temporary)); value = json.loads(args.receiver_custody.read_text())
        value["templates"]["event"] = "push"; write_json(args.receiver_custody, value)
        refusal(lambda: STAGE.assemble(args), "producer run custody differs")
    # Actual production assembler consumes mandatory exact current custody, not globals.
    for mutation in ("old-sdd", "old-templates", "wrong-repository", "missing-field", "zero-run", "changed-archive", "changed-package", "v1-descriptor", "lower-floor"):
        with tempfile.TemporaryDirectory(prefix="p4-current-receiver-refusal-") as temporary:
            args = fixture(Path(temporary)); value = json.loads(args.receiver_custody.read_text())
            if mutation == "old-sdd": value["sdd"]["version"] = "2.0.3"
            elif mutation == "old-templates": value["templates"]["version"] = "0.16.0"
            elif mutation == "wrong-repository": value["sdd"]["repository"] = "foreign/repo"
            elif mutation == "missing-field": value["sdd"].pop("packageSha256")
            elif mutation == "zero-run": value["sdd"]["runId"] = 0
            elif mutation == "changed-archive": value["sdd"]["artifactDigest"] = "sha256:" + "0"*64
            elif mutation == "changed-package": value["sdd"]["packageSha256"] = "0"*64
            elif mutation in ("v1-descriptor", "lower-floor"):
                text = args.descriptor.read_text().replace('contractVersion: "2.0.0"', 'contractVersion: "1.1.0"') if mutation == "v1-descriptor" else args.descriptor.read_text().replace('version: "2.1.0"', 'version: "2.0.3"')
                args.descriptor.write_text(text); STAGE.DESCRIPTOR["sha256"] = STAGE.digest(args.descriptor)
            write_json(args.receiver_custody, value)
            try: STAGE.assemble(args)
            except ValueError: pass
            else: raise AssertionError(f"accepted {mutation}")
            assert not args.output.exists()
    for raw in ('{}', '[]', '{"schema":"x","schema":"x"}', ' ' * 8193):
        try: STAGE.parse_receiver_custody(raw)
        except ValueError: pass
        else: raise AssertionError("accepted missing/ambiguous/unbounded custody")
    print("portable provider staging checks passed")


if __name__ == "__main__": main()
