"""The shared domain package's test suite. START HERE.

    tests/
      paths.py       where packages/insolvia_core is, counted ONCE
      unit/          pytest over the domain modules and both adapter families   every PR

One tier, and by design. The domain is pure; the AWS adapters are tested
against monkeypatched boto3 clients (the testing skill's "monkeypatch the
transport" rule), and the same adapters are exercised against real tables and
a real bucket by every consumer that runs against an environment — the seed
loader on every deploy, and services/api's `tests/integration/` tier over HTTP.
A second live-store suite here would prove the same round trips under a
developer's credentials, which are wider than any deployed role's.

`unit/` is what a bare `pytest` runs (`testpaths` in pyproject.toml) and what
the `Core package` PR check runs. ADR 0021 owns the tiers.
"""
