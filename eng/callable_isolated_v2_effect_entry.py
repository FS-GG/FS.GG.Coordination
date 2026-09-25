"""Installed closed effect-scaffold entry; it has no authority or effects."""

from __future__ import annotations

import json
import sys

import callable_isolated_v2_effect_closed as closed

SCHEMA = "fsgg.coordination.callable-isolated-v2-effect-scaffold-refusal/1"


def main(argv: list[str] | None = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments == ["execute-native-pull"]:
        result = closed.execute_native_pull(None, None)
    else:
        result = closed.ClosedResult("entry-closed")
    print(json.dumps({"schema": SCHEMA, "reason": result.reason,
                      "authorized": result.authorized,
                      "canDispatch": result.can_dispatch,
                      "liveEffects": result.live_effects},
                     sort_keys=True, separators=(",", ":")))
    return 78


if __name__ == "__main__":
    raise SystemExit(main())
