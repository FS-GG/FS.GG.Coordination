"""Read-only direct entry for the callable isolated v2 runtime archive."""

from __future__ import annotations

import sys


def _check_imports() -> bool:
    from callable_isolated_v2_runtime import adapters, contracts, coordinator, grant

    operator = contracts.operator_module()
    return (
        coordinator.operator is operator
        and callable(adapters.execute_installed)
        and callable(adapters.recover_installed)
        and callable(grant.verify)
        and operator.OPERATION_IDENTITY
        == "v2-call-01-4b-isolated-native-v2-provisional"
    )


def main(arguments: list[str]) -> int:
    if arguments != ["--check-imports"]:
        print("callable-isolated-v2-runtime: direct invocation refused", file=sys.stderr)
        return 64
    if not _check_imports():
        print("callable-isolated-v2-runtime: import identity mismatch", file=sys.stderr)
        return 70
    print("fsgg.gs2-09-9-v5-runtime-candidate/1")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
