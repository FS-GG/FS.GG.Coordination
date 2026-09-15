#!/usr/bin/env python3
import os
import sys
import xml.etree.ElementTree as ET

path, obligation, started_ns = sys.argv[1:]
if not os.path.isfile(path):
    raise SystemExit(f"{obligation} test census missing")
if os.stat(path).st_mtime_ns + 1_000_000_000 < int(started_ns):
    raise SystemExit(f"{obligation} test census is stale")
root = ET.parse(path).getroot()
counters = next((node for node in root.iter() if node.tag.endswith("Counters")), None)
if counters is None:
    raise SystemExit(f"{obligation} test census missing")
total = int(counters.attrib.get("total", "0"))
passed = int(counters.attrib.get("passed", "0"))
failed = int(counters.attrib.get("failed", "0"))
if total <= 0 or passed != total or failed != 0:
    raise SystemExit(f"{obligation} test census refused: total={total} passed={passed} failed={failed}")
print(f"{obligation} test census passed: total={total}")
