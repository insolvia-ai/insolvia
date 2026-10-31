# integration — the API's integration tier

A pytest suite that signs in to a real Cognito pool and drives a **running**
API over HTTPS: tokens from the real pool, the Lambda's own role, the bucket
policy, the database grants, and ADR 0009's tenancy refusals seen from
outside. It imports no backend code (it is an HTTP client), which is why it
outlived the Python backend it was written beside (ADR 0028, plan 18.23).

| Target | Runs it | Who exists |
|---|---|---|
| staging | `api-staging.yml`'s `integration` job, after the alias shift | `seeds/staging.json` |
| dev | `./integration/scripts/dev-test.sh`, against `scripts/dev-up.sh`'s API on :8080 | `seeds/dev.json` |

Locally it needs `./scripts/dev-aws-setup.sh` (writes `backend/.env` with the
shared dev pool's ids) and `./scripts/dev-aws-seed.sh` (the account, and the
seeded firm in the local Postgres). The first run builds `integration/.venv`
from `requirements.txt`; any Python 3.12 or later will do.

`tests/conftest.py` owns the rules: the opt-in gate, the inputs it refuses to
guess, and the one scratch case per environment.
