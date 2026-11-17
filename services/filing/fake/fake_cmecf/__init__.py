"""A fake CM/ECF — the court a laptop has (ADR 0024, "Three environments").

DEV AND CI ONLY. It lives outside `src/` so the worker's image never contains
it (the Dockerfile copies `src/insolvia_filing` alone, and
tests/unit/test_packaging.py holds it to that); nothing under `src/` imports
it (tests/unit/test_architecture.py). It is never deployed to staging or
production — see services/filing/README.md for why staging has none either.
"""

from .server import FAULTS, FakeCmEcf

__all__ = ["FAULTS", "FakeCmEcf"]
