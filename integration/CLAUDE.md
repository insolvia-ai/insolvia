# integration/ — agent rules

The API's integration tier: pytest over HTTP against a running API, signed in
through the real pool. Human docs: [`README.md`](README.md). Which tier a test
belongs in: the `insolvia-testing` skill and
[ADR 0021](../docs/adr/0021-test-tiers-and-seed-fixtures.md).

- **HTTP only.** Never import backend code or reach a database, bucket or
  queue directly; assert what the API answers. A store's behaviour is the
  .NET data tier's to test (`backend/tests`).
- **No credentials in the tree, no defaults for targets.** Every input comes
  from the environment (`tests/conftest.py`); the password is
  `E2E_TEST_USER_PASSWORD`, a GitHub environment secret on CI.
- **Never write to seeded data.** Writes go to the one scratch case
  (`SCRATCH_COURT`) and are undone in teardown; fixture cases are read.
- **Staging runs from CI only** (`api-staging.yml`); a laptop runs the dev
  target through `scripts/dev-test.sh`.
- `requirements.txt` is the whole toolchain. No lint gate covers this
  directory since the Python services' `ruff` steps went with them.
