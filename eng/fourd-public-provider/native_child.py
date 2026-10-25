#!/usr/bin/env python3
"""Same-PID exec only after the owner records actual birth in its typed protocol."""
import os
import sys

gate = int(sys.argv[1])
permission = os.read(gate, 2)
os.close(gate)
if permission != b"1" or len(sys.argv) < 3 or not os.path.isabs(sys.argv[2]):
    raise SystemExit(125)
os.execve(sys.argv[2], sys.argv[2:], dict(os.environ))
