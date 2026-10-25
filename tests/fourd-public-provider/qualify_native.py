#!/usr/bin/env python3
"""Reproduce native source correspondence with the actual apphost; no native effects.

--evidence must name a new directory. Only generated models/ITFs, model-state
observations, hashes, the selected public producer DLL and run logs survive there.
Fixture Git/context/capsule bytes and build intermediates stay in a separate temporary
workspace. This checks source behavior; publication/adoption/native acceptance remain open.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
QUINT_VERSION = "0.32.0"
SEED = "20261025"
PROJECT = HERE / "native-typed/FourD.Native.Tests.fsproj"
RUNTIME_NAMES = ("FourD.Typed", "FourD.Typed.dll", "FourD.Typed.deps.json",
                 "FourD.Typed.runtimeconfig.json", "FSharp.Core.dll")
SCENARIOS = (
    ("journey", "FourDNativeJourney", "journeyStep", 50,
     ("witnessNativeSuccess", "witnessArchiveAdmitted")),
    ("capability", "FourDNativeCapabilityJourney", "capabilityStep", 50, ("witnessCapability",)),
    ("archive-mutation", "FourDNativeArchiveMutationJourney", "faultStep", None,
     ("witnessArchiveMutationRefused",)),
    ("whole", "FourDNativeWholeJourney", "wholeStep", 50, ("witnessWholeAccepted",)),
    ("cancelled", "FourDNativeCancelledJourney", "faultStep", 50, ("witnessCancelledPlaintext",)),
    ("source-mutation", "FourDNativeMutationJourney", "faultStep", 50, ("witnessChangedGeneration",)),
    ("lost-ack", "FourDNativeLostAckJourney", "faultStep", 50,
     ("witnessUnacknowledged", "witnessSettledUnknown")),
)


def canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode()


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            value.update(block)
    return value.hexdigest()


def named_digest(paths):
    value = hashlib.sha256()
    for path in sorted(paths):
        value.update(path.relative_to(ROOT).as_posix().encode() + b"\0")
        value.update(bytes.fromhex(digest(path)) + b"\0")
    return value.hexdigest()


def write(path, value):
    path.write_bytes(canonical(value))


def command_prefix(explicit):
    prefix = explicit
    if prefix is None:
        prefix = json.loads(os.environ.get("FSGG_FOURD_QUINT_COMMAND", '["quint"]'))
    if (not isinstance(prefix, list) or not 1 <= len(prefix) <= 16
            or any(not isinstance(arg, str) or not arg or len(arg) > 4096 for arg in prefix)):
        raise ValueError("Quint command must be a bounded JSON argv list or --quint argv")
    return prefix


class Qualification:
    def __init__(self, producer, quint, evidence):
        self.producer, self.quint, self.evidence = producer, quint, evidence
        self.stage, self.runs, self.profiles = "initialization", [], {}
        self.started = time.monotonic()
        self.env = dict(os.environ, DOTNET_PROCESSOR_COUNT="1", RAYON_NUM_THREADS="1",
                        NODE_OPTIONS="--max-old-space-size=768", DOTNET_CLI_TELEMETRY_OPTOUT="1",
                        DOTNET_NOLOGO="1", PYTHONDONTWRITEBYTECODE="1")
        self.summary = {"schema": "fsgg.fourd.native-source-qualification/1", "status": "running",
                        "sampled": True, "exhaustive": False, "nativeAccepted": False,
                        "seed": SEED, "samples": 5000, "steps": 60}

    def run(self, name, argv, *, timeout=60, extra_env=None):
        self.stage = name
        env = self.env if extra_env is None else dict(self.env, **extra_env)
        started = time.monotonic()
        path = self.evidence / (name + ".log")
        try:
            with path.open("wb") as output:
                done = subprocess.run(argv, cwd=ROOT, env=env, stdout=output,
                                      stderr=subprocess.STDOUT, timeout=timeout, check=False)
        except subprocess.TimeoutExpired:
            self.runs.append(dict(name=name, argv=argv, exitCode=None, timedOut=True,
                                  elapsedSeconds=round(time.monotonic() - started, 3), log=path.name))
            write(self.evidence / "runs.json", self.runs)
            raise
        self.runs.append(dict(name=name, argv=argv, exitCode=done.returncode,
                              elapsedSeconds=round(time.monotonic() - started, 3), log=path.name))
        write(self.evidence / "runs.json", self.runs)
        if done.returncode:
            raise RuntimeError(name + " failed; retained " + path.name)
        return path

    def qualify(self, work):
        # Source qualification uses one CPU without changing the selected native profile.
        if not hasattr(os, "sched_getaffinity"):
            raise RuntimeError("Linux CPU affinity is required")
        os.sched_setaffinity(0, {min(os.sched_getaffinity(0))})
        if self.producer.name != "FourD.Typed" or not self.producer.is_file():
            raise ValueError("--producer must select the actual standalone FourD.Typed apphost")
        runtime = {name: digest(self.producer.parent / name) for name in RUNTIME_NAMES}
        selected = self.evidence / "selected-producer.dll"
        shutil.copyfile(self.producer.parent / "FourD.Typed.dll", selected)
        version_log = self.run("quint-version", [*self.quint, "--version"], timeout=10)
        version = version_log.read_text().strip()
        if version != QUINT_VERSION:
            raise ValueError("Quint " + QUINT_VERSION + " is required; observed " + version)
        entries = {}
        for index, arg in enumerate(self.quint):
            resolved = shutil.which(arg) if index == 0 else arg
            if resolved and Path(resolved).is_file():
                path = Path(resolved).resolve(strict=True)
                entries[str(path)] = digest(path)
        tool = dict(version=version, argv=self.quint, entrypoints=entries)
        tool_hash = hashlib.sha256(canonical(tool)).hexdigest()
        model = self.evidence / "FourDNativeOperation.qnt"
        self.run("generate-model", [str(self.producer), "generate-native-quint", str(model)])
        model_hash = digest(model)
        self.run("typecheck", [*self.quint, "typecheck", str(model)])
        profile = dict(modelSha256=model_hash, module="FourDNativeOperation", init="init", step="step",
                       invariant="invariant", seed=SEED, samples=5000, steps=60, version=version)
        self.profiles["sampled"] = profile
        self.run("sampled-5000", [*self.quint, "run", str(model), "--main", profile["module"],
                                 "--invariant", "invariant", "--max-samples", "5000", "--max-steps", "60",
                                 "--seed", SEED, "--verbosity", "1"])
        self.stage = "actual-fixtures"
        spec = importlib.util.spec_from_file_location("fourd_native_fixture", HERE / "test_native_protocol.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        fixture_started = time.monotonic()
        fixture_metrics = []
        observations = {}
        for name in ("journey", "capability", "archive-mutation"):
            fixture_root = work / name
            fixture_root.mkdir()
            fixture = module.NativeFixture(fixture_root, [str(self.producer)],mode="capability" if name=="capability" else "full")
            fixture.trace_zero = True
            fixture.root_evidence["nowUnixSeconds"] = fixture.started
            if name == "capability":
                fixture.prepared()
                result=fixture.finish()
                if result["state"]["disposition"]!="capability-only" or result["state"]["qualified"]:
                    raise RuntimeError("actual capability profile classification differs")
            elif name == "journey":
                fixture.image_archive_ready()
                image = fixture.ok("image", path=str(fixture.image_path))
                expected = dict(fixture.image, temporaryMountNamespaceExited=True, postNamespaceIdentityVerified=True)
                if image.get("imageBinding") != expected:
                    raise RuntimeError("actual producer did not publish its validated P2 binding")
                fixture.ok("revalidate-source", evidence=fixture.source)
                fixture.ok("revalidate-p2", evidence=fixture.p2proof)
                fixture.intent("p2")
                fixture.ok("p2", path=str(fixture.p2_path))
                # Demonstrate that historical proof replay does not retain plaintext.
                shutil.rmtree(fixture.context)
                fixture.git_path.unlink()
                fixture.archive_path.unlink()
                Path(fixture.image["archivePath"]).unlink()
                Path(fixture.p2proof["executorDllPath"]).unlink()
                result = fixture.finish()
                if not result["state"]["successful"] or result["state"]["wholeAccepted"]:
                    raise RuntimeError("actual local journey/result classification differs")
            else:
                fixture.image_archive_ready()
                fixture.make_native_evidence(layer_bytes=b"source qualification second OCI generation")
                code, result = fixture.event("archive", path=str(fixture.archive_path))
                if (code != 2 or result["state"]["refusal"] != "native-archive-rebind-refused"
                        or result["modelState"]["archive"] != 1 or result["modelState"]["currentArchive"] != 2
                        or "load-image" in result["actions"]):
                    raise RuntimeError("actual acquired archive generation was not refused")
            path = self.evidence / (name + "-observations.json")
            write(path, fixture.observed)
            observations[name] = path
            fixture_metrics.append(dict(scenario=name, calls=fixture.counter, transitions=len(fixture.observed)))
        (self.evidence / "actual-fixtures.log").write_text(
            "Actual standalone producer fixtures; input workspace is temporary and excluded.\n"
            + "\n".join(item["scenario"] + ": " + str(item["transitions"]) + " observed transitions, "
                          + str(item["calls"]) + " producer calls" for item in fixture_metrics) + "\n")
        self.runs.append(dict(name="actual-fixtures", argv=[str(self.producer), "native-transition"], exitCode=0,
                              elapsedSeconds=round(time.monotonic() - fixture_started, 3),
                              log="actual-fixtures.log", scenarios=fixture_metrics))
        write(self.evidence / "runs.json", self.runs)
        traces = {}
        for name, module, step, steps, witnesses in SCENARIOS:
            if steps is None:
                steps = len(json.loads(observations[name].read_bytes()))
            selected_trace = self.evidence / (name + "-{seq}.itf.json")
            profile = dict(modelSha256=model_hash, module=module, init="initCapability" if name=="capability" else "init", step=step,
                           invariant="invariant", seed=SEED, samples=1, steps=steps, witnesses=list(witnesses))
            self.profiles[name] = profile
            log = self.run("witness-" + name, [*self.quint, "run", str(model), "--main", module,
                           "--init",profile["init"],"--step", step, "--invariant", "invariant", "--max-samples", "1",
                           "--max-steps", str(steps), "--seed", SEED, "--n-traces", "1",
                           "--out-itf", str(selected_trace), "--verbosity", "1", "--witnesses", *witnesses])
            text = log.read_text()
            for witness in witnesses:
                if not re.search(re.escape(witness) + r" was witnessed in [1-9][0-9]* trace\(s\)", text):
                    raise RuntimeError(name + ": witness not reached: " + witness)
            trace = Path(str(selected_trace).replace("{seq}", "0"))
            if not trace.is_file():
                raise RuntimeError(name + ": Quint did not emit its selected ITF")
            traces[name] = trace
        write(self.evidence / "profiles.json", self.profiles)
        props = ["-p:BaseIntermediateOutputPath=" + str(work / "obj") + "/",
                 "-p:BaseOutputPath=" + str(work / "bin") + "/"]
        self.run("restore-replay", ["dotnet", "restore", str(PROJECT), "--locked-mode", *props], timeout=90)
        self.run("build-replay", ["dotnet", "build", str(PROJECT), "--configuration", "Release",
                                 "--no-restore", "--nologo", *props], timeout=90)
        replay_root = work / "bin/Release/net10.0"
        replay = replay_root / "FourD.Native.Tests"
        fsquint = replay_root / "FsQuint.dll"
        contract_files = [ROOT / "eng/fourd-public-provider/typed" / name for name in (
            "NativeJoin.fs", "NativePolicy.fs", "NativeSource.fs", "NativeOperationModel.fs", "NativeProtocol.fs", "Program.fs")]
        adapter_files = [Path(__file__).resolve(), HERE / "test_native_protocol.py", HERE / "typed/NativeProtocolTests.fs",
                         PROJECT, PROJECT.parent / "Program.fs", PROJECT.parent / "packages.lock.json"]
        fixed = dict(FSGG_FOURD_QUINT_TOOL_SHA256=tool_hash,
                     FSGG_FOURD_QUINT_CONTRACT_SHA256=named_digest(contract_files),
                     FSGG_FOURD_QUINT_ADAPTER_SHA256=named_digest(adapter_files),
                     FSGG_FOURD_QUINT_IMPLEMENTATION_SHA256=runtime["FourD.Typed.dll"])
        identities = dict(tool=tool, toolFingerprint=tool_hash, modelSha256=model_hash,
                          runtimeFiles=runtime, contractFiles={p.relative_to(ROOT).as_posix(): digest(p) for p in contract_files},
                          adapterFiles={p.relative_to(ROOT).as_posix(): digest(p) for p in adapter_files},
                          fsQuintVersion="0.1.0", fsQuintDllSha256=digest(fsquint),
                          replayDllSha256=digest(replay_root / "FourD.Native.Tests.dll"), fingerprints=fixed)
        write(self.evidence / "identities.json", identities)
        counts = {}
        for name in observations:
            extra = dict(fixed, FSGG_FOURD_NATIVE_QUINT_ITF=str(traces[name]),
                         FSGG_FOURD_NATIVE_QUINT_OBSERVATIONS=str(observations[name]),
                         FSGG_FOURD_QUINT_PROFILE_SHA256=hashlib.sha256(canonical(self.profiles[name])).hexdigest())
            self.run("replay-" + name, [str(replay)], extra_env=extra)
            counts[name] = len(json.loads(observations[name].read_bytes()))
        if runtime != {name: digest(self.producer.parent / name) for name in RUNTIME_NAMES}:
            raise RuntimeError("producer closure changed during qualification")
        self.summary.update(status="passed", modelSha256=model_hash,
                            implementationSha256=runtime["FourD.Typed.dll"], actualTransitions=counts,
                            deterministicWitnesses=len(SCENARIOS), fsQuintVersion="0.1.0",
                            elapsedSeconds=round(time.monotonic() - self.started, 3))
        write(self.evidence / "summary.json", self.summary)
        print("native source qualification: PASS (5000 sampled traces; " + str(sum(counts.values())) + " actual transitions)")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--producer", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True, help="new directory for public source evidence")
    parser.add_argument("--quint", nargs="+", help="Quint 0.32.0 executable and any prefix arguments")
    args = parser.parse_args(argv)
    try:
        producer = args.producer.expanduser().resolve(strict=True)
        prefix = command_prefix(args.quint)
        evidence = args.evidence.expanduser().resolve()
        evidence.mkdir(parents=True, exist_ok=False, mode=0o700)
    except (OSError, ValueError):
        parser.error("select an existing producer, bounded Quint argv and a fresh evidence directory")
    gate = Qualification(producer, prefix, evidence)
    try:
        with tempfile.TemporaryDirectory(prefix="fourd-native-source-") as temporary:
            gate.qualify(Path(temporary))
        return 0
    except Exception as error:
        # Fixture assertion payloads can contain acquisition requests. Never put them in
        # public artifacts; retain the failing stage and its already bounded process log.
        gate.summary.update(status="failed", stage=gate.stage, errorType=type(error).__name__,
                            elapsedSeconds=round(time.monotonic() - gate.started, 3))
        if isinstance(error, AssertionError) and error.args:
            payload = error.args[0]
            if isinstance(payload, tuple) and len(payload) == 2 and isinstance(payload[1], dict):
                reason = payload[1].get("refusal")
                if isinstance(reason, str) and re.fullmatch(r"native-[a-z0-9-]{1,120}", reason):
                    gate.summary["refusal"] = reason
        write(evidence / "summary.json", gate.summary)
        print("native source qualification failed at " + gate.stage + ": " + type(error).__name__, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
