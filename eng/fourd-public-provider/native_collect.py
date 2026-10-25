#!/usr/bin/env python3
"""Collect only declared native metadata; never copy source, keys or plaintext."""
import hashlib
import json
import os
from pathlib import Path
import sys

session=Path(sys.argv[1]);destination=Path(sys.argv[2])
value=json.loads(session.read_bytes())
configuration=value["config"]
state=Path(configuration["imageState"])
inventory=[]
for root,names in ((state,("preflight.json","native-image-binding.json","cleanup.json")),
                   (destination,("p2-result.json","p2-journal.tar"))):
    for name in names:
        path=root/name
        if not path.exists():continue
        fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
        with os.fdopen(fd,"rb") as source:
            digest=hashlib.sha256();count=0
            for block in iter(lambda:source.read(65536),b""):
                count+=len(block)
                if count>16*1024*1024:raise SystemExit("native-collection-bound-refused")
                digest.update(block)
        inventory.append({"name":name,"bytes":count,"sha256":digest.hexdigest()})
target=destination/"collection.json"
with target.open("xb") as output:
    os.chmod(target,0o600)
    output.write((json.dumps({"schema":"fsgg.fourd.native-collection/1","files":inventory},sort_keys=True,separators=(",",":"))+"\n").encode())
    output.flush();os.fsync(output.fileno())
