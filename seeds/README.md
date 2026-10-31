# `seeds/` — what a seeded environment holds

Two kinds of file, one loader, no code here. The loader is the .NET
migrator's `seed` command
([`backend/src/Insolvia.Migrator/Seeding/`](../backend/src/Insolvia.Migrator/Seeding/),
[ADR 0028](../docs/adr/0028-backend-moves-to-dotnet-and-postgres.md)): on
staging it runs **inside the VPC** as the migrator Lambda, because the
database is private; locally it is `dotnet run` (or the compose migrator)
against `backend/docker-compose.yml`'s Postgres. Its ids are uuid5,
byte-identical to the deleted Python seeder's, so an environment seeded
before the switch converges on the same records.

| File | Says | Loaded by |
|---|---|---|
| `dev.json` | Who exists on a developer's machine — the dev account's firm — and which fixture case it holds, with the portal client bound to it ([ADR 0023](../docs/adr/0023-client-portal-identity-and-isolation.md)) | `scripts/dev-aws-seed.sh` (real subjects from the shared dev pool, after `dev-aws-setup.sh`), or — with no `backend/.env` — `docker compose up` in `backend/` (subjects derived from the addresses: rows, nobody to sign in as) |
| `staging.json` | Who exists in staging — three people across two firms, so cross-tenant isolation is testable — which fixture case they hold, and the portal client bound to it | the staging migrator Lambda: once per release in the migrator leg (`migrator-staging.yml`, `{}` = migrate then seed), and again as `{"command": "seed"}` from `.github/actions/seed-staging` before the API's integration tier and the browser suite |
| `fixtures/<version>/cases.json` | What each fixture case contains: chapter, court and division (a reference into the court registry, `backend/src/Insolvia.Core/Regulatory/courts`), debtors, collection items, documents — in the API's own request-body shapes, with `{"$ref": "claims/<handle>"}` wherever one record names another (a fixture publishes no ids) | the loader, when an environment fixture names one of its cases |
| `fixtures/<version>/manifest.json` | Every sample document's size and sha256 | the loader, to verify a copy landed as published |
| `fixtures/<version>/objects/` | The sample documents themselves — small, synthetic, committed | `scripts/dev-fixture.sh publish` (the migrator's `seed-publish`), which copies them into the shared fixture bucket the staging loader copies from server-side (created by insolvia-services' `infra/envs/shared`); locally the loader reads this committed tree directly |

**Every load converges.** Accounts are created if absent and their password set
on every run; firms are keyed on the people in them; a case's id is derived
from the environment, the firm, the version and the case's `handle`, so a
second run finds the rows it wrote and writes nothing twice. Rows that exist
are left alone — a seeded environment is somewhere people work — which also
means **no test may write to a fixture case**: a re-seed will not undo it, and
the integration tier, which finds fixture cases by what the seed wrote, fails
the next run saying the case "was modified after seeding". Suites write to
their own scratch cases (`integration/tests/conftest.py`,
`e2e/support/scratch-case.ts`). And **a changed fixture is a new version**,
curated by hand (copy the latest `fixtures/<version>/`, edit it — the
Python `capture` that read one out of a dev stack was not ported, 18.23) and
published with `scripts/dev-fixture.sh publish <version>` after its PR
merges. A debtor's tax id is typed from the SSA's never-issued block, never
copied from anywhere (the API only ever shows its last four anyway).
The migrator's tests (`backend/tests/Insolvia.Migrator.Tests`) load
`dev.json` and `staging.json`, with every version they name — so a fixture
that would not load fails `Backend (.NET)` on the PR, not the next staging
deploy. **The staging seed runs from the migrator IMAGE**: `seeds/` is a
named build context of `backend/Dockerfile`'s migrator stage, so a seeds
change rebuilds that image (release.yml's migrator leg) and staging loads
the commit's own fixture.

**Every Debtor 1 and Debtor 2 is a firm client's copy** (from `v4`,
[ADR 0022](../docs/adr/0022-a-client-is-not-a-case.md)). A version's
`cases.json` carries a top-level `clients` list — the firm's directory
entries, in `POST /v1/firm/clients`'s body shape plus a `handle` — and a
debtor names one with `"client_id": {"$ref": "clients/<handle>"}`. The loader
derives a client's id like a case's, writes it before the case, and refuses
a fixture whose Debtor 1 or Debtor 2 names none; `v1`–`v3` are therefore
not loadable any more. Their rows are the pre-client data the migrator's
`purge-pre-client` command deletes (dry run unless `--apply`) —
`scripts/dev-aws-seed.sh` runs it before every load, and a second run
deletes nothing. Staging and prod were wiped at the switch (ADR 0028), so
neither holds any. (A fixture case's `clients` in `seeds/<env>.json` are
something else: portal logins, ADR 0023.)

**Nothing here is real.** Every address ends in `.test` (RFC 2606 — it can never
be a mailbox); every name, employer and figure is invented; every document
describes nobody. The loader refuses production before it connects (and the
production migrator's role holds no Cognito, S3 or case-key grant to seed
with — `infra/modules/migrator`). The one secret — the shared test-user password — lives in
the `insolvia-staging` GitHub environment (the migrator deploy puts it in
the staging migrator Lambda's environment) and in
`~/.config/insolvia/dev.env` for a laptop, never in a file here.

The reasoning: [ADR 0021](../docs/adr/0021-test-tiers-and-seed-fixtures.md).
The suites that read these files: [`e2e/`](../e2e/README.md) and
[`integration/`](../integration/README.md).
