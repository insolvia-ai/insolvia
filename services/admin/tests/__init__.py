"""The admin service's test suite. START HERE.

    tests/
      conftest.py    the app over memory adapters, real staff tokens
      paths.py       where services/admin is, counted ONCE
      unit/          pytest over core/, the routes, and the seed loader   every PR

One tier today. `unit/` is what a bare `pytest` runs (`testpaths` in
pyproject.toml), what `scripts/dev-test.sh` runs, and what the `Admin service`
PR check runs. The seed loader (`entrypoints/seed.py`) is exercised here
against memory stores and a fake pool; its real-AWS behaviour is proven by the
environments that run it — every staging deploy, every `dev-aws-seed.sh`.

An `integration/` tier, when one earns its place, follows services/api's
shape: its own directory, its own conftest, gated on `INSOLVIA_INTEGRATION=1`,
and aimed at a running service rather than an in-process one. ADR 0021 owns
the tiers.
"""
