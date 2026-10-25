#!/usr/bin/env python3
"""First-dispatch carrier and bounded IO dispatch for the existing native producer."""
from __future__ import annotations

import argparse
import base64
import importlib.util
import json
import os
from pathlib import Path
import signal
import sys
import tarfile
import time

sys.dont_write_bytecode = True
sys.path.insert(0,str(Path(__file__).resolve().parent))
from native_io import (Effects, HeldFile, NativeClient, NativeIOError, OwnedDirectory,
                       acquire_capsule, capsules, git_observation, private_directory,
                       safe_environment, write_new)
from native_bootstrap import GitHubReader


def wait_packet(client: NativeClient, producer: dict, locator: dict, *, reader, run_id: int,
                placement: str, actor: int, sleep=time.sleep):
    """Wait only within the original typed window; a partial/foreign packet refuses."""
    client.observe("producer", evidence=producer)
    client.require_action("wait-packet")
    ordinal = 0
    while True:
        state = client.require_action("wait-packet")
        deadline = client.started + state["deadlines"]["preflight"]
        status, metadata, _ = reader.request(
            f"/repos/{locator['repository']}/releases/tags/{locator['tagPrefix']}-{run_id}",
            1024*1024, deadline)
        if status == 404:
            client.observe("wait")
            state = client.require_action("wait-packet")
            sleep(min(5, max(0, state["deadlines"]["preflight"]-client.elapsed())))
            continue
        if status != 200:
            raise NativeIOError("native-packet-release-read-unknown")
        release = capsules.closed_json(metadata, 1024*1024)
        matches = [asset for asset in release.get("assets", []) if asset.get("name") == locator["packetName"]]
        if len(matches) != 1 or type(matches[0].get("id")) is not int:
            raise NativeIOError("native-packet-locator-ambiguous")
        ordinal += 1
        release_path, packet_path = client.work/f"release-{ordinal}.json", client.work/f"packet-{ordinal}.json"
        write_new(release_path, metadata)
        status, packet_raw, _ = reader.request(
            f"/repos/{locator['repository']}/releases/assets/{matches[0]['id']}",
            1024*1024, deadline, "application/octet-stream")
        if status != 200:
            raise NativeIOError("native-packet-asset-read-unknown")
        write_new(packet_path, packet_raw)
        run_status, run_raw, _ = reader.request(f"/repos/FS-GG/FS.GG.FourD/actions/runs/{run_id}",1024*1024,deadline)
        job_status, jobs_raw, links = reader.request(
            f"/repos/FS-GG/FS.GG.FourD/actions/runs/{run_id}/attempts/1/jobs?per_page=100",1024*1024,deadline)
        if run_status != 200 or job_status != 200:
            raise NativeIOError("native-first-run-read-unknown")
        run_path,jobs_path=client.work/f"run-{ordinal}.json",client.work/f"jobs-{ordinal}.json"
        write_new(run_path,run_raw);write_new(jobs_path,jobs_raw)
        evidence={"packetPath":str(packet_path),"releasePath":str(release_path),"packetAssetId":matches[0]["id"],
                  "locator":locator,"runPath":str(run_path),"jobPath":str(jobs_path),"jobLinkHeaders":links,
                  "expectedRunId":run_id,"expectedPlacementSha":placement,"expectedActorId":actor,
                  "nowUnixSeconds":client.started_unix+client.elapsed()}
        root = client.call("native-root",{"schema":"fsgg.fourd.native-root-request/1","producer":producer,"root":evidence})
        if root.get("schema") != "fsgg.fourd.native-root-join/1":
            raise NativeIOError("native-root-packet-refused")
        # The same acquired evidence is consumed by the stateful protocol.
        evidence["nowUnixSeconds"]=client.started_unix+client.elapsed()
        result=client.observe("packet",evidence=evidence)
        if result.get("refusal"):
            raise NativeIOError("native-root-transition-refused")
        return root["packet"]


def prepare_context(source: Path, target: Path, effects: Effects, *, deadline: float, client=None):
    """Git archive is a local transformation after encrypted source acquisition."""
    private_directory(target)
    if client is not None:client.own_directory("context",OwnedDirectory(target))
    context=target/"source"
    context.mkdir(mode=0o700)
    archive=HeldFile.create(target/"source.tar",b"")
    try:
        remaining=deadline-time.monotonic()
        response=effects.run(["/usr/bin/git","archive","--format=tar",f"--output=/proc/self/fd/{archive.fd}","HEAD"],
                             cwd=source,env=safe_environment(Path("/nonexistent")),timeout=min(30,remaining-36),
                             total_timeout=remaining,capture=None,pass_fds=(archive.fd,))
        if response.returncode:
            raise NativeIOError("native-context-archive-child-failed")
        # Source capsule bounds the full source payload to8MiB/512files. A32MiB
        # local tar bound includes headers/padding without an unbounded read.
        raw=archive.read(32*1024*1024)
        import io
        previous=os.umask(0o077)
        try:
            with tarfile.open(fileobj=io.BytesIO(raw),mode="r:") as package:
                package.extractall(context,filter="data")
        finally:
            os.umask(previous)
        return context
    finally:
        if effects.settle():
            if not archive.wipe_close():
                raise NativeIOError("native-context-archive-retirement-unknown")


def source_evidence(client: NativeClient, context: Path, source: Path, effects: Effects):
    path=client.work/f"git-observation-{time.monotonic_ns()}.json"
    deadline=client.started+client.response["state"]["deadlines"]["preflight"]
    # Image/P2 consumption uses its already-issued phase allowance rather than
    # renewing the acquisition interval.
    phase=client.response["state"]["phase"]
    if phase in ("preflight-ready","image-running","image-ready"):
        deadline=client.started+client.response["state"]["deadlines"]["image" if phase!="image-ready" else "p2"]
    revision,tree,held=git_observation(source,path,effects,safe_environment(Path("/nonexistent")),lambda:deadline-time.monotonic())
    client.transient_evidence.append(held)
    return {"sourceRoot":str(context),"observedRevision":revision,"observedTree":tree,"gitObservationPath":str(path)}


def run(args, *, reader=None, consumer=None):
    private_directory(args.private)
    work=args.private/"native-control"
    producer=capsules.closed_json(args.producer_evidence.read_bytes(),65536)
    client=NativeClient(Path(producer["runtimeRoot"])/"FourD.Typed",work,mode=args.mode,
                        started=args.started,started_unix=args.started_unix,host=Path(producer["hostPath"]))
    client.config={"private":str(args.private),"coordinationSource":str(Path(__file__).resolve().parents[2])}
    client.own_directory("private",OwnedDirectory(args.private))
    # Pop before invoking any child. Bootstrap acquisition ran in a separate
    # workflow step that never received either secret.
    encoded_key=os.environ.pop("FSGG_FOURD_NATIVE_RECIPIENT_KEY_B64","")
    token=os.environ.pop("GH_TOKEN","")
    reader=reader or GitHubReader(token,cancelled=lambda:client.cancelled)
    effects=Effects()
    previous={}
    def cancel(*unused):
        client.signal();effects.request_cancel()
    for selected in (signal.SIGINT,signal.SIGTERM):
        previous[selected]=signal.signal(selected,cancel)
    try:
        locator=capsules.closed_json(args.locator.read_bytes(),65536)
        packet=wait_packet(client,producer,locator,reader=reader,run_id=args.run_id,
                           placement=args.expected_head,actor=args.actor_id)
        client.require_action("acquire-capsule")
        # The key remains only in this parent until the native capsule/root join
        # returns expose-recipient-key inside acquire_capsule.
        key=capsules.strict_b64(encoded_key,16*1024)
        encoded_key=""
        def read(kind,identity,limit,deadline):
            endpoint=(f"/repos/FS-GG/FS.GG.Coordination/releases/{identity}" if kind=="release"
                      else f"/repos/FS-GG/FS.GG.Coordination/releases/assets/{identity}")
            status,raw,_=reader.request(endpoint,limit,deadline,
                "application/vnd.github+json" if kind=="release" else "application/octet-stream")
            if status!=200: raise NativeIOError("native-capsule-read-unknown")
            return raw
        deadline=client.started+client.response["state"]["deadlines"]["preflight"]
        source=acquire_capsule(packet,key,args.private,effects,deadline=deadline,client=client,read=read)
        key=b""
        context=prepare_context(source,args.private/"context",effects,deadline=deadline,client=client)
        evidence=source_evidence(client,context,source,effects)
        response=client.observe("source",evidence=evidence)
        if response.get("refusal"):
            raise NativeIOError("native-reconstructed-source-refused")
        client.own_directory("snapshot",OwnedDirectory(context))
        for directory,_folders,files in os.walk(context,topdown=False):
            for name in files:
                path=Path(directory)/name
                path.chmod(0o500 if path.stat().st_mode&0o111 else 0o400)
            Path(directory).chmod(0o500)
        client.config.update({"sourceGit":str(source),"contextSource":str(context),"private":str(args.private),
                       "coordinationSource":str(Path(__file__).resolve().parents[2]),
                       "p2Source":str(args.p2_source),"executorDll":str(args.executor_dll),"akka":str(args.akka),
                       "packet":packet,"result":str(args.result)})
        client.save()
        if consumer is None:
            # Import only from the acquired, typed-validated clean snapshot.
            path=context/"eng/portable-workspace/native_typed.py"
            spec=importlib.util.spec_from_file_location("fourd_acquired_native_consumer",path)
            module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
            consumer=module.run
        return consumer(client,args)
    except BaseException as error:
        if client.cancelled:
            client.observe("cancel")
        else:
            client.observe("refuse",reason=(str(error) if isinstance(error,NativeIOError) else "native-io-failure"))
        if "begin-cleanup" in client.response.get("actions",[]):client.observe("begin-cleanup")
        # The real consumer installs its guaranteed emergency cleanup. Before
        # source acquisition, only this bounded mechanism scope can own effects.
        end=client.started+client.response["state"]["operationDeadlines"]["cleanup"]
        settled=effects.settle(deadline=end)
        if settled:
            for holder in client.owned_directories.values():
                if holder.path==args.private or holder.path.name=="credentials":continue
                try:
                    client.retire_directory(holder,deadline=end)
                except BaseException:pass  # Missing cleanup evidence stays incomplete.
                finally:holder.close()
        write_new(args.result,client.response)
        return client.response.get("state",{}).get("exitCode",2)
    finally:
        for selected,handler in previous.items():signal.signal(selected,handler)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--producer-evidence",type=Path,required=True)
    parser.add_argument("--locator",type=Path,required=True)
    parser.add_argument("--private",type=Path,required=True)
    parser.add_argument("--result",type=Path,required=True)
    parser.add_argument("--started",type=float,required=True)
    parser.add_argument("--started-unix",type=int,required=True)
    parser.add_argument("--mode",choices=("full","capability"),required=True)
    parser.add_argument("--run-id",type=int,required=True)
    parser.add_argument("--actor-id",type=int,required=True)
    parser.add_argument("--expected-head",required=True)
    parser.add_argument("--p2-source",type=Path,required=True)
    parser.add_argument("--executor-dll",type=Path,required=True)
    parser.add_argument("--akka",type=Path,required=True)
    raise SystemExit(run(parser.parse_args()))


if __name__=="__main__":main()
