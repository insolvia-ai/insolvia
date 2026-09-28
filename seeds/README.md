# `seeds/` — what a seeded environment holds

Two kinds of file, one loader
(`services/admin/src/insolvia_admin/entrypoints/seed.py`), no code here.

| File | Says | Loaded by |
|---|---|---|
| `dev.json` | Who exists on a developer's machine — the dev account's firm — and which fixture case it holds, with the portal client bound to it ([ADR 0023](../docs/adr/0023-client-portal-identity-and-isolation.md)) | `scripts/dev-aws-seed.sh`, by hand, after `dev-aws-setup.sh` |
| `staging.json` | Who exists in staging — three people across two firms, so cross-tenant isolation is testable — which fixture case they hold, and the portal client bound to it | `.github/actions/seed-staging`, on every staging deploy, before the API's integration tier and the browser suite |
| `fixtures/<version>/cases.json` | What each fixture case contains: chapter, court and division (a reference into the court registry, `insolvia_core.courts`), debtors, collection items, documents — in the API's own request-body shapes, with `{"$ref": "claims/<handle>"}` wherever one record names another (a fixture publishes no ids) | the loader, when an environment fixture names one of its cases |
| `fixtures/<version>/manifest.json` | Every sample document's size and sha256 | the loader, to verify a copy landed as published |
| `fixtures/<version>/objects/` | The sample documents themselves — small, synthetic, committed | `scripts/dev-fixture.sh publish`, which copies them into the shared fixture bucket the loader reads from |

**Every load converges.** Accounts are created if absent and their password set
on every run; firms are keyed on the people in them; a case's id is derived
from the target table, the firm, the version and the case's `handle`, so a
second run finds the rows it wrote and writes nothing twice. Rows that exist
are left alone — a seeded environment is somewhere people work — which also
means **no test may write to a fixture case**: a re-seed will not undo it, and
the integration tier, which finds fixture cases by what the seed wrote, fails
the next run saying the case "was modified after seeding". Suites write to
their own scratch cases (`services/api/tests/integration/conftest.py`,
`e2e/support/scratch-case.ts`). And **a changed fixture is a new version**, captured out of a dev stack with
`scripts/dev-fixture.sh capture v2 <case-id> <handle>` and published after its
PR merges. Capture drops a debtor's tax id (the API only ever shows its last
four); type it back from the SSA's never-issued block before committing.
`services/admin`'s unit tier loads `dev.json` and `staging.json`, with every
version they name, into memory stores — so a fixture that would not load
fails `scripts/dev-test.sh` there, not the next staging deploy.

**Every Debtor 1 and Debtor 2 is a firm client's copy** (from `v4`,
[ADR 0022](../docs/adr/0022-a-client-is-not-a-case.md)). A version's
`cases.json` carries a top-level `clients` list — the firm's directory
entries, in `POST /v1/firm/clients`'s body shape plus a `handle` — and a
debtor names one with `"client_id": {"$ref": "clients/<handle>"}`. The loader
derives a client's id like a case's, writes it before the case, and refuses
a fixture whose Debtor 1 or Debtor 2 names none; `v1`–`v3` are therefore
not loadable any more. Their rows are the pre-client data
`services/admin` `entrypoints/purge_pre_client.py` deletes —
`scripts/dev-aws-seed.sh` runs it before every load, and a second run
deletes nothing. (A fixture case's `clients` in `seeds/<env>.json` are
something else: portal logins, ADR 0023.)

**Nothing here is real.** Every address ends in `.test` (RFC 2606 — it can never
be a mailbox); every name, employer and figure is invented; every document
describes nobody. The loader refuses a production table, and `capture` refuses
any source that is not a dev stack. The one secret — the shared test-user
password — lives in a GitHub environment for staging and in
`~/.config/insolvia/dev.env` for a laptop, never in a file here.

The reasoning: [ADR 0021](../docs/adr/0021-test-tiers-and-seed-fixtures.md).
The suites that read these files: [`e2e/`](../e2e/README.md) and
[`services/api/tests/integration/`](../services/api/tests/integration/).
