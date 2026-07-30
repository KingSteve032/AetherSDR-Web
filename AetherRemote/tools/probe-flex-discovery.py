#!/usr/bin/env python3
"""Listen briefly for station-local FLEX discovery advertisements."""

import argparse
import socket
import time


def printable(payload: bytes) -> str:
    return "".join(chr(value) if 32 <= value < 127 else "." for value in payload)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=15)
    parser.add_argument("--count", type=int, default=3)
    args = parser.parse_args()
    if not 1 <= args.seconds <= 120 or not 1 <= args.count <= 20:
        parser.error("seconds or count is outside the safe probe range")

    deadline = time.monotonic() + args.seconds
    seen: set[str] = set()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("", 4992))
        print(f"Listening on UDP 4992 for {args.seconds} seconds...", flush=True)
        while len(seen) < args.count:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            listener.settimeout(remaining)
            try:
                payload, sender = listener.recvfrom(8192)
            except TimeoutError:
                break
            identity = f"{sender[0]}:{printable(payload)}"
            if identity in seen:
                continue
            seen.add(identity)
            print(f"{sender[0]} sent {len(payload)} bytes")
            print(printable(payload))

    print(f"Unique advertisements: {len(seen)}")
    return 0 if seen else 2


if __name__ == "__main__":
    raise SystemExit(main())
