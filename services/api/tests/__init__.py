"""The API's test suite. START HERE.

    tests/
      conftest.py    the app over memory adapters (shared by every tier)
      paths.py       where services/api is, counted ONCE
      unit/          pytest over core/ and the Flask test client   every PR
      integration/   the RUNNING API over HTTP, signed in    INSOLVIA_INTEGRATION=1

**Each tier is a directory because conftest inheritance is scoped by
directory** — so "which tier am I in" and "which guards apply to me" are one
question. `unit/` is what a bare `pytest` runs (`testpaths` in pyproject.toml),
what `scripts/dev-test.sh` runs, what the pre-push hook runs, and what the
`API service` PR check runs. It touches no network and needs no AWS.

`integration/` is skipped at collection unless `INSOLVIA_INTEGRATION=1`, and
it aims at a deployed-or-running API rather than an in-process one: this
machine's dev stack through `scripts/dev-test-integration.sh`, or staging
from `api-staging.yml` after the alias shift. Its conftest owns the rules.

There is deliberately no `smoke/` tier here. The post-deploy checks that run
against PRODUCTION are unauthenticated — prod holds real case data and no test
identity — and they live with the browser suite in `e2e/tests/smoke/`, which
`app-prod.yml` runs as the release's last leg. ADR 0021 owns the tiers.
"""
