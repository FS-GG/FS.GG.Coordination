#!/usr/bin/env python3
"""Guarded forward Execution pair release route (dormant until separately admitted).

state | helper | select-version | bind | preflight | packages | public-load | finalize

Live modes need an enabled admission, the pinned shared journal engine and credentials
supplied only to the job that owns them. This entry point never retries a mutation.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
F = sys.modules.get("execution_pair_forward")
if F is None:
    spec = importlib.util.spec_from_file_location("execution_pair_forward", HERE / "execution_pair_forward.py")
    F = importlib.util.module_from_spec(spec)
    sys.modules["execution_pair_forward"] = F
    spec.loader.exec_module(F)

Refused = F.Refused


def environ(name: str) -> str:
    value = os.environ.get(name, "")
    F.require(bool(value), f"{name} is required")
    return value


def publisher_context(selection: dict) -> dict:
    F.require(os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch", "publisher event differs")
    F.require(os.environ.get("GITHUB_REPOSITORY") == F.REPOSITORY, "publisher repository differs")
    F.require(os.environ.get("GITHUB_REF") == "refs/heads/main", "publisher branch differs")
    F.require(os.environ.get("GITHUB_RUN_ATTEMPT") == "1", "publisher rerun is not admitted")
    F.require(os.environ.get("GITHUB_ACTOR") == selection["operator"], "publisher operator differs")
    F.require(os.environ.get("OPERATION") == selection["operation"], "operation input differs from the admitted selection")
    return {"sha": F.hexid(environ("GITHUB_SHA"), 40, "publisher"), "runId": F.positive(int(environ("GITHUB_RUN_ID")), "run id")}


def git(*args) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True, timeout=60).stdout.strip()


def bind_publisher(selection: dict, publisher_sha: str) -> None:
    source = selection["source"]
    ancestor = subprocess.run(["git", "merge-base", "--is-ancestor", source, publisher_sha], capture_output=True, timeout=60).returncode == 0
    paths = git("diff", "--name-only", source, publisher_sha).splitlines()
    F.bind_publisher(selection, publisher_sha, paths, ancestor=ancestor, tree=git("rev-parse", source + "^{tree}"))


def helper_root(admission: dict, workdir: pathlib.Path) -> pathlib.Path:
    override = os.environ.get("FSGG_SHARED_HELPER_ROOT")
    if override:
        F.helper_digest_report(pathlib.Path(override), admission["sharedHelper"])
        return pathlib.Path(override)
    return F.fetch_shared_helper(admission["sharedHelper"], workdir / "shared-helper")


def selected(admission: dict, *, live: bool) -> dict:
    state = F.effective_state(admission)
    F.require(state == "enabled" or (state == "selected" and not live), f"forward route is {state}; live effects are not admitted")
    return admission["selection"]


def ledger_wiring(engine, selection):
    api_class = F.make_api_class(engine)
    ledger_api = api_class(environ("ORDINARY_LEDGER_TOKEN"))
    ref = F.journal_ref(selection)
    return api_class, ledger_api, ref


def push_runner(argv):
    subprocess.run(argv, check=True, capture_output=True, text=True, timeout=900)


def verify_signature(path):
    result = subprocess.run(["dotnet", "nuget", "verify", str(path), "--all"], capture_output=True, text=True, timeout=60)
    F.require(result.returncode == 0, "repository signature verification failed")


def installed_check(directory, mode, output):
    harness = F.harness_module()
    harness["check_execution_pair"](pathlib.Path(directory), mode, pathlib.Path(output))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["state", "helper", "select-version", "bind", "preflight", "packages", "public-load", "finalize"])
    parser.add_argument("--admission", type=pathlib.Path, default=F.ROOT / F.ADMISSION_PATH)
    parser.add_argument("--workdir", type=pathlib.Path)
    parser.add_argument("--observed", type=pathlib.Path, help="select-version: JSON {versions:[...], tags:[...]}")
    parser.add_argument("--result", type=pathlib.Path)
    args = parser.parse_args(argv)
    try:
        F.require(sys.flags.optimize == 0, "protected validators require Python optimize=0")
        admission = F.load_admission(args.admission)
        pin = admission["sharedHelper"]
        if args.command == "state":
            print(F.effective_state(admission))
            return 0
        if args.command == "select-version":
            F.require(args.observed is not None, "--observed is required")
            observed = F.strict_json(args.observed.read_bytes())
            print(F.select_version(admission["proposedVersion"], observed.get("versions", []), observed.get("tags", [])))
            return 0
        F.require(args.workdir is not None and not args.workdir.exists(), "a fresh --workdir is required")
        args.workdir.mkdir(parents=True)
        if args.command == "helper":
            F.fetch_shared_helper(pin, args.workdir)
            print(f"shared helper pinned at {pin['commit']} verified")
            return 0
        live = args.command in {"packages", "finalize"}
        selection = selected(admission, live=live)
        transport = F.UrllibTransport()
        if args.command == "bind":
            bind_publisher(selection, publisher_context(selection)["sha"])
            print("publisher binding verified")
            return 0
        context = publisher_context(selection)
        bind_publisher(selection, context["sha"])
        github_token = environ("GH_TOKEN")
        engine = F.load_engine(helper_root(admission, args.workdir), pin)
        api_class = F.make_api_class(engine)
        if args.command == "preflight":
            policy_raw = F.read_policy(api_class(environ("POLICY_TOKEN")))
            receipt = F.run_preflight(transport, selection, token=github_token, policy_raw=policy_raw, workdir=args.workdir)
            ledger = api_class(github_token)
            fresh, physical = engine.route.classify_journal_destination(ledger, F.journal_ref(selection))
            F.require(fresh, "the selected release journal is not unused")
            receipt["journal"] = {"state": "unused", "ref": F.journal_ref(selection), "authorityMain": physical}
            (args.result or args.workdir / "preflight-receipt.json").write_bytes(F.canonical(receipt) + b"\n")
            print("forward preflight passed; no journal, tag, feed or release changed")
            return 0
        if args.command == "public-load":
            candidate = F.acquire_candidate(transport, github_token, selection, args.workdir)
            receipt = F.run_public_load(selection, candidate, transport=transport, verify_signature=verify_signature,
                                        installed_check=installed_check, workdir=args.workdir)
            (args.result or args.workdir / "public-load-receipt.json").write_bytes(receipt)
            print("forward public package execution passed")
            return 0
        # packages / finalize: shared protected journal.
        stage = args.command
        candidate = F.acquire_candidate(transport, github_token, selection, args.workdir)
        _, ledger_api, ref = ledger_wiring(engine, selection)
        route = engine.route
        journal = engine.journal.ProtectedReleaseJournal(ledger_api, ref, main_directory=True)
        publisher_api = api_class(github_token)
        generated = None
        nuget_key = os.environ.get("NUGET_API_KEY", "") if stage == "packages" else ""
        if stage == "finalize":
            generated = F.derive_finalize_inputs(transport, github_token, selection, candidate, context["runId"], args.workdir)
        else:
            # The first irreversible job re-authenticates the four receivers itself; it does not trust an earlier job.
            F.acquire_receivers(transport, github_token, selection, args.workdir)
        effects = F.plan_effects(engine, selection, candidate, generated, stage)
        allowed = F.sendable(stage)
        policy_reader = (lambda: F.read_policy(api_class(environ("POLICY_TOKEN")))) if stage == "finalize" else None
        pair_admission = F.PairAdmission(publisher_api, selection, effects, stage=stage, publisher_sha=context["sha"], run_id=context["runId"],
                                         actor=os.environ["GITHUB_ACTOR"], ref=os.environ["GITHUB_REF"], policy_reader=policy_reader)
        admission_gate = route.ProtectedPublisherAdmission(pair_admission, ledger_api, ref)
        provider = F.PairProvider(engine, publisher_api, selection, candidate, transport=transport, github_token=github_token,
                                  nuget_key=nuget_key, generated=generated, asset_dir=args.workdir / "assets", runner=push_runner, allowed=allowed)
        cid, intent = F.content_id(selection), F.journal_intent(selection)
        fresh, physical = route.classify_journal_destination(ledger_api, ref)
        if stage == "packages" and fresh:
            F.occupancy_join(selection, F.observe_occupancy(transport, selection, github_token))
            F.require(admission_gate.authorize(cid, "journal", "intent", cid), "forward journal admission denied")
            for effect in effects:
                F.require(provider.observe(effect).state == "absent", f"{effect.identity}: preexisting or unreadable effect")
            F.require(provider.component_absent(), "component tag or release already exists")
            F.require(route.authority_main(ledger_api) == physical, "Authority main moved before forward genesis")
            F.require(admission_gate.authorize(cid, "journal", "intent", cid), "forward genesis protection changed")
            journal.initialize(intent)
        else:
            F.require(not fresh, "continuing a stage requires the existing forward journal")
            F.require(route.authority_main(ledger_api) == physical, "Authority main moved before forward recovery")
            journal.read()
            F.require(route.authority_main(ledger_api) == physical, "Authority main moved during forward recovery")
        journal.validate_intent(intent)
        F.run_stage(engine, stage, cid, effects, journal, admission_gate, provider)
        print(f"forward {stage} stage complete")
        return 0
    except (Refused, OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
        print(f"Forward Execution pair route refused: {error}", file=sys.stderr)
        return 1
    except RuntimeError as error:  # the shared engine modules each define their own Refused
        if type(error).__name__ == "Refused":
            print(f"Forward Execution pair route refused: {error}", file=sys.stderr)
            return 1
        raise


if __name__ == "__main__":
    raise SystemExit(main())
