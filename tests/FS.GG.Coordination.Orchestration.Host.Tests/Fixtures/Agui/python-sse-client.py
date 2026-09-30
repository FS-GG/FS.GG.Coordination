#!/usr/bin/env python3
import json
import sys


def main() -> int:
    events = []
    for block in sys.stdin.read().replace("\r\n", "\n").split("\n\n"):
        data = "\n".join(line[5:].lstrip() for line in block.splitlines() if line.startswith("data:"))
        if data:
            events.append(json.loads(data))

    assert [event["type"] for event in events] == ["RUN_STARTED", "STATE_SNAPSHOT", "RUN_FINISHED"]
    assert events[0]["protocolVersion"] == "1.0"
    snapshot = events[1]["snapshot"]
    assert snapshot["schema"] == "fsgg.coordination.agui-work-item-projection/1"
    assert snapshot["authority"] == "durable-work-item-replay"
    assert snapshot["partial"] is False
    assert snapshot["generation"] == "3"
    assert snapshot["deliveryVerified"] is False
    assert events[2]["result"] == snapshot
    print(json.dumps({"eventCount": len(events), "sequence": snapshot["journalSequence"]}, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
