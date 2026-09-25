"""Proposed v5 vault entry with every authority and effect path closed.

This file is outside the installed archive. A future runnable revision needs a
new independently reviewed source, artifact, workflow and protected grant.
"""

from __future__ import annotations

import dataclasses
import json
import sys
from typing import Protocol

SCHEMA = "fsgg.coordination.callable-isolated-v2-v5-vault-refusal/1"


class FutureVaultTokenPort(Protocol):
    """Future protected handle-to-token operation; no implementation here."""

    def read_token_for_public_handle(self, public_handle_id: str,
                                     public_credential_id: str) -> bytes: ...


@dataclasses.dataclass(frozen=True)
class ClosedDecision:
    reason: str
    schema: str = dataclasses.field(init=False, default=SCHEMA)
    authorized: bool = dataclasses.field(init=False, default=False)
    can_dispatch: bool = dataclasses.field(init=False, default=False)
    live_effects: int = dataclasses.field(init=False, default=0)
    exit_code: int = dataclasses.field(init=False, default=78)


def inspect_closed(_public_identity: object, _request_spec: object,
                   _vault_or_effect_ports: object,
                   grant: object) -> ClosedDecision:
    """Refuse before inspecting records, grant contents or any protected port."""
    if grant is None:
        return ClosedDecision("no-grant")
    return ClosedDecision("protected-vault-unavailable")


def main(argv: list[str] | None = None) -> int:
    """Standalone proposed entry, always exit 78 without token discovery."""
    arguments = list(sys.argv[1:] if argv is None else argv)
    result = (inspect_closed(None, None, None, None)
              if arguments == ["execute-native-pull"]
              else ClosedDecision("entry-closed"))
    print(json.dumps({"schema": result.schema, "reason": result.reason,
                      "authorized": result.authorized,
                      "canDispatch": result.can_dispatch,
                      "liveEffects": result.live_effects},
                     sort_keys=True, separators=(",", ":")))
    return result.exit_code


if __name__ == "__main__":
    raise SystemExit(main())
