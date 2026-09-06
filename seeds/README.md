# `seeds/` — what a seeded environment holds

Two kinds of file, one loader
(`services/admin/src/insolvia_admin/entrypoints/seed.py`), no code here.

| File | Says | Loaded by |
|---|---|---|
| `dev.json` | Who exists on a developer's machine — the dev account's firm — and which fixture case it holds | `scripts/dev-aws-seed.sh`, by hand, after `dev-aws-setup.sh` |
| `staging.json` | Who exists in staging — three people across two firms, so cross-tenant isolation is testable — and which fixture case they hold | `.github/actions/seed-staging`, on every staging deploy, before the API's integration tier and the browser suite |
| `fixtures/<version>/cases.json` | What each fixture case contains: chapter and district, debtors, collection items, documents — in the API's own request-body shapes | the loader, when an environment fixture names one of its cases |
| `fixtures/<version>/manifest.json` | Every sample document's size and sha256 | the loader, to verify a copy landed as published |
| `fixtures/<version>/objects/` | The sample documents themselves — small, synthetic, committed | `scripts/dev-fixture.sh publish`, which copies them into the shared fixture bucket the loader reads from |

**Every load converges.** Accounts are created if absent and their password set
on every run; firms are keyed on the people in them; a case's id is derived
from the target table, the firm, the version and the case's `handle`, so a
second run finds the rows it wrote and writes nothing twice. Rows that exist
are left alone — a seeded environment is somewhere people work — so **a changed
fixture is a new version**, captured out of a dev stack with
`scripts/dev-fixture.sh capture v2 <case-id> <handle>` and published after its
PR merges.

**Nothing here is real.** Every address ends in `.test` (RFC 2606 — it can never
be a mailbox); every name, employer and figure is invented; every document
describes nobody. The loader refuses a production table, and `capture` refuses
any source that is not a dev stack. The one secret — the shared test-user
password — lives in a GitHub environment for staging and in
`~/.config/insolvia/dev.env` for a laptop, never in a file here.

The reasoning: [ADR 0021](../docs/adr/0021-test-tiers-and-seed-fixtures.md).
The suites that read these files: [`e2e/`](../e2e/README.md) and
[`services/api/tests/integration/`](../services/api/tests/integration/).
