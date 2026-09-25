"""Closed native-effect boundary. No protected authority port is installed."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol


class FutureEffectPorts(Protocol):
    """Names reserved for a separately reviewed installed effect adapter."""

    def read_execution_token(self) -> bytes: ...

    def reserve_attempt(self) -> object: ...

    def post_pull(self) -> object: ...


@dataclass(frozen=True)
class ClosedResult:
    reason: str
    authorized: bool = False
    can_dispatch: bool = False
    live_effects: int = 0


def execute_native_pull(grant: bytes | None,
                        _ports: FutureEffectPorts | None) -> ClosedResult:
    """Refuse before touching any port, even when caller supplies grant bytes."""
    if grant is None:
        return ClosedResult("no-grant")
    return ClosedResult("protected-authority-unavailable")
