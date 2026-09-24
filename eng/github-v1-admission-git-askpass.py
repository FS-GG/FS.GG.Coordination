#!/usr/bin/env python3
"""Git askpass for the fixed admission remote; secret arrives on inherited FD."""

import os
import re
import sys


def main() -> int:
    prompt = sys.argv[1] if len(sys.argv) == 2 else ""
    if re.fullmatch(r"Username for 'https://github\.com(?:/[^']*)?': ?", prompt):
        print("x-access-token")
        return 0
    if not re.fullmatch(r"Password for 'https://x-access-token@github\.com(?:/[^']*)?': ?", prompt):
        return 3
    try:
        fd = int(os.environ["FSGG_ADMISSION_TOKEN_FD"])
        token = os.read(fd, 8193)
    except (KeyError, OSError, ValueError):
        return 3
    if not 0 < len(token) <= 8192 or b"\n" in token or b"\0" in token:
        return 3
    try:
        sys.stdout.write(token.decode("ascii") + "\n")
    except UnicodeError:
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
