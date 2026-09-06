"""The mailer's test suite. START HERE.

    tests/
      conftest.py    the local service registry every test runs under
      paths.py       where services/mailer is, counted ONCE
      unit/          pytest over core/, the adapters, and the ingress   every PR

One tier today. `unit/` is what a bare `pytest` runs (`testpaths` in
pyproject.toml), what `scripts/dev-test.sh` runs, and what the `Mailer
service` PR check runs. The deployed service is smoke-tested by
`mailer-staging.yml` and `mailer-prod.yml` after each deploy.

An `integration/` tier, when one earns its place, follows services/api's
shape: its own directory, its own conftest, gated on `INSOLVIA_INTEGRATION=1`.
ADR 0021 owns the tiers.
"""
