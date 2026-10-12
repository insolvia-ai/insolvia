"""`python -m fake_cmecf [--port 8790] [--fault none]` — the fake on loopback,
in the foreground (services/filing/scripts/dev-up.sh runs it). Prints its
URL; the fake account's values are served at /__fake/account, never
printed."""

from __future__ import annotations

import argparse

from .server import FAULTS, FakeCmEcf


def main() -> None:
    parser = argparse.ArgumentParser(prog="fake_cmecf")
    parser.add_argument("--port", type=int, default=8790)
    parser.add_argument("--fault", choices=FAULTS, default="none")
    args = parser.parse_args()
    court = FakeCmEcf(fault=args.fault, port=args.port)
    print(
        f"fake CM/ECF listening on {court.base_url} (fault: {args.fault})", flush=True
    )
    print(f"state: {court.base_url}/__fake/state", flush=True)
    try:
        court.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        court.stop()


if __name__ == "__main__":
    main()
