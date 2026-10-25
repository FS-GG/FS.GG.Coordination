#!/usr/bin/env python3
"""Attempt original owned retirement; never declare qualification or renew its clock."""
from __future__ import annotations
import argparse
import os
from pathlib import Path
import shutil
import sys
import time

sys.dont_write_bytecode=True
sys.path.insert(0,str(Path(__file__).resolve().parent))
from native_io import Effects,HeldFile,NativeClient,OwnedDirectory,capsules,mechanisms,safe_environment,write_new


def retire(session:Path,evidence:Path):
    if not session.exists():
        write_new(evidence,{"schema":"fsgg.fourd.native-emergency-retirement/1","observedSession":False,"failures":[]})
        return 0
    client=NativeClient.load(session)
    if client.response and client.response.get("state",{}).get("phase")=="finished":
        write_new(evidence,{"schema":"fsgg.fourd.native-emergency-retirement/1","observedSession":True,
                            "nativeAlreadyTerminal":True,"failures":[]})
        return 0
    failures=[]
    # This is still the original window. A late observer retains Unknown rather
    # than starting another cleanup command with a fresh allowance.
    end=client.started+(5400 if client.mode=="full" else 1200)
    if time.monotonic()>=end:
        write_new(evidence,{"schema":"fsgg.fourd.native-emergency-retirement/1","observedSession":True,
                            "originalWindowExpired":True,"failures":["original-window-expired"]})
        return 2
    scope=Effects()
    for intent in (client.response or {}).get("state",{}).get("intents",[]):
        pid,ticks=intent.get("pid"),intent.get("startIdentity")
        if pid is None or ticks is None:continue
        status,current=mechanisms._process_observation(pid)
        if status=="live" and current.start_ticks==int(ticks):scope.owned[pid]=current
        elif status=="unknown":failures.append("owned-child-identity-unknown")
        # A reused PID is not the old identity and grants no signalling right.
    try:settled=scope.settle()
    except BaseException:settled=False
    if not settled:failures.append("owned-child-settlement-unknown")
    owned=client.config.get("ownedDirectories",{})
    private=Path(client.config["private"])
    expected=owned.get(str(private),{}).get("identity")
    try:
        root=OwnedDirectory(private)
        if root.identity!=expected:raise RuntimeError("private-root-replaced")
    except BaseException:
        failures.append("private-root-identity-unknown");root=None
    if settled and root is not None:
        # A fully acquired source may contain private-store cleanup mechanisms.
        # Authenticate the complete context again before executing its cleanup.
        context=client.config.get("contextSource")
        packet=client.config.get("packet")
        if context and packet and Path(context).is_dir() and client.config.get("imageState"):
            product=packet["product"]
            proof=client.call("validate-native-source",{"schema":"fsgg.fourd.native-source-request/1",
                "expectedRevision":product["revision"],"expectedTree":product["tree"],"expectedInventorySha256":product["inventorySha256"],
                "observedRevision":product["revision"],"observedTree":product["tree"],"sourceRoot":context})
            if proof.get("accepted") is True:
                remaining=end-time.monotonic()
                if remaining>36:
                    env=safe_environment(client.work,client.host)
                    env.update(FSGG_FOURD_NATIVE_SESSION=str(session),FSGG_FOURD_NATIVE_COORDINATION_SOURCE=client.config["coordinationSource"],
                               FOURD_PHASE_DEADLINE=str(end))
                    argv=["/usr/bin/python3","-I","-B",str(Path(context)/"eng/portable-workspace/native-image.py"),"cleanup",
                          "--state",client.config["imageState"],"--podman","/usr/bin/podman",
                          "--build-runroot",client.config["buildRunroot"],"--fresh-runroot",client.config["freshRunroot"],
                          "--image-tmpdir",client.config["imageTmpdir"]]
                    try:
                        result=scope.run(argv,cwd=client.work,env=env,timeout=remaining-36,total_timeout=remaining,capture=None,allow_cancelled=True)
                        if result.returncode:failures.append("private-store-cleanup-unknown")
                    except BaseException:failures.append("private-store-cleanup-unknown")
            else:failures.append("cleanup-source-bytes-unknown")
        for name,identity in client.config.get("ownedFiles",{}).items():
            path=Path(name)
            if not path.exists():continue
            try:
                held=HeldFile.open(path)
                if (held.device,held.inode)!=(identity["device"],identity["inode"]):
                    os.close(held.fd);failures.append("credential-path-replaced");continue
                if not held.wipe_close():failures.append("credential-retirement-unknown")
            except BaseException:failures.append("credential-retirement-unknown")
        for name,record in owned.items():
            if record["role"] not in ("source","context"):continue
            path=Path(name)
            if not path.exists():continue
            try:
                holder=OwnedDirectory(path)
                try:
                    if holder.identity!=record["identity"]:raise RuntimeError("owned-directory-replaced")
                    for directory,_folders,_files in os.walk(path):Path(directory).chmod(0o700)
                    shutil.rmtree(path)
                finally:holder.close()
            except BaseException:failures.append("plaintext-tree-retirement-unknown")
        root.close()
    write_new(evidence,{"schema":"fsgg.fourd.native-emergency-retirement/1","observedSession":True,
                        "ownedScopeSettled":settled,"failures":failures})
    return 0 if not failures else 2


if __name__=="__main__":
    parser=argparse.ArgumentParser();parser.add_argument("--session",type=Path,required=True);parser.add_argument("--evidence",type=Path,required=True)
    args=parser.parse_args();raise SystemExit(retire(args.session,args.evidence))
