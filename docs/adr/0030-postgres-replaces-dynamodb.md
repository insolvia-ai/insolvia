# ADR 0030 — PostgreSQL replaces DynamoDB; the backend stays Python

- **Status:** Accepted 2026-11-16, decided by the maintainer in chat. Not
  built: the build breakdown below is the work. The hosting and ordering
  calls were recommended to the maintainer and accepted as recommended.
- **Date:** 2026-11-16
- **Numbering:** 0028 and 0029 belonged to the dropped .NET rewrite and are
  not reused, so links in closed issues keep pointing at what they meant.
- **Amends:** plan decision **D6** (Python + Flask + Mangum on Lambda stands;
  only its data store changes),
  [ADR 0008](0008-testing-shape-follows-the-code-it-tests.md) (store tests run
  against a real Postgres),
  [ADR 0018](0018-sqs-queue-and-worker-lambda-over-step-functions.md) (the job
  record is a Postgres row, not a case-table item),
  [ADR 0021](0021-test-tiers-and-seed-fixtures.md) (the seeder writes
  Postgres), [ADR 0022](0022-a-client-is-not-a-case.md) (the firm table and
  its `by-client` index become tables and an index).
- **Stands unchanged:** [0001](0001-client-stays-dumb-trust-boundary.md),
  [0009](0009-a-case-belongs-to-a-firm.md),
  [0012](0012-shared-python-domain-package.md) (the domain stays in
  `insolvia_core`, free of drivers),
  [0023](0023-client-portal-identity-and-isolation.md),
  [0024](0024-electronic-filing-path.md) (vault isolation, now in two layers),
  [0027](0027-a-cases-chapter-is-history.md) (built on the new schema).
- **Relates to:** the .NET rewrite's ADR 0028 (dropped; readable at
  `refs/pull/606/head`), whose data and hosting design this ADR carries over
  without its language change.

## Decision

**Every DynamoDB table is replaced by one PostgreSQL 17 database per
environment. The backend stays Python.** The HTTP contract does not change,
and neither do the store ports in `insolvia_core/ports.py`: each DynamoDB
adapter is replaced by a Postgres adapter behind the same port, and the
in-memory twins stay.

### Access

- **psycopg 3**, plain SQL, no ORM. The stores are small and their queries
  are few; SQL reads better than a mapping layer and keeps the adapter the
  one place a query lives.
- **Migrations are numbered `.sql` files** under
  `insolvia_core/adapters/postgres/migrations/`, applied in order by a small
  runner that records each in a `schema_migrations` table. Forward-only.
- **Connection:** one `DATABASE_URL` setting per service, read in its config
  like every other setting. Unset fails closed (the service refuses to start
  outside tests), never falls back to memory silently.

### Schema

- **One table per entity**, replacing the single-table partitions. Case
  entities are keyed `(case_id, id)`, firm entities `(firm_id, id)`.
- **Nested shapes are `jsonb`:** an entity's `body` and its `provenance`.
  The domain keeps treating them as documents and validating them in code;
  confirm-before-entry and provenance stay domain invariants
  ([`case-data-model.md`](../reference/case-data-model.md)), not SQL ones.
- Money is `numeric`, timestamps `timestamptz`, court dates `date` (never
  converted to UTC).
- **Tenancy is a column:** `firm_id` on every tenant row, with composite
  foreign keys so a case's children cannot point across firms. No row-level
  security in v1; authorization stays in the domain (ADR 0009).

### Behaviour the stores must keep

- Each DynamoDB conditional write (status compare-and-set, the packet's
  `updatedAt` lock, single-use approvals, never-twice filing transitions)
  becomes `UPDATE … WHERE <condition>`; zero rows affected raises the same
  domain error `ConditionalCheckFailed` maps to today.
- Each multi-item transaction (create a case with its history, bind a client,
  merge clients, swap the approval pointer) becomes one database transaction.
- Index cursors become keyset cursors. Cursors are already opaque to clients,
  so the wire shape survives.

### Sealed data and logs

- **KMS envelopes are kept byte for byte.** The tax-id cipher and the filing
  credential vault keep `GenerateDataKey`, their encryption contexts and the
  AES-256-GCM envelope; Postgres stores the envelope as `bytea` and never sees
  plaintext.
- **Vault isolation is two layers:** KMS (only the filing role decrypts, as
  today) and database grants (the vault table grants `INSERT`/`DELETE` to the
  API role and `SELECT` to the filing role alone).
- **Append-only logs** (case access log, admin audit) are tables the
  application roles may only `INSERT` into.
- **The grants are tested.** A store test connects as each application role
  and asserts what it may and may not do. The KMS key policies get an offline
  test of the Terraform's policy documents (who may decrypt what).

### Environments

| | Database | Other AWS |
|---|---|---|
| **Local** | Postgres 17 in Docker, migrated on start, seeded by the admin seeder | **None but the shared dev Cognito pool.** S3, SQS and KMS get local stand-ins (an S3-compatible container; the in-process queue with a local poller; a development-only key adapter refused outside `INSOLVIA_ENV=local`) |
| **Tests** | Postgres in Docker on a laptop, a service container in CI | — |
| **Dev (per machine)** | `infra/envs/dev` loses its tables; it is optional for local work | its bucket, queues and key, for checking real IAM and KMS |
| **Staging / prod** | **Aurora Serverless v2, PostgreSQL 17, minimum capacity 0**, one cluster per environment, storage encrypted under a customer-managed key, in a VPC (two AZs, private subnets, one fck-nat instance for egress). IAM database authentication, one Postgres role per service (`api`, `worker`, `admin`, `mcp`, `filing`, `migrator`). A one-shot migrator Lambda applies migrations before each alias shift | unchanged |

The Terraform for staging and prod is written and validated, **not applied
while AWS is paused** ([runbook](../runbooks/aws-paused.md)). The account
prerequisites (the deploy role's EC2/RDS/Secrets Manager actions) are a PR to
insolvia-services, applied by a human when AWS comes back.

### Cut-over

- **No data migration.** The product is not live and holds no real data;
  when AWS returns, staging and prod are created fresh and seeded.
- **Store by store, through ordinary PRs to `main`.** Each PR moves one store
  group to Postgres, deletes its DynamoDB adapter and its table, and keeps
  `main` working locally. There is no long-lived branch and no period where
  both stores hold the same data.
- **The before-first-real-data builds (#501, #503, #506, #507, #509) are
  built on Postgres**, after the store they touch has moved.

## Context

- **Why the maintainer chose this:** the next builds are relational (one
  person in several firms, plan versions, a lifecycle history) and a single
  DynamoDB table makes those joins and constraints awkward; and a Docker
  Postgres runs on a laptop as the production engine, which the repo's
  local-testability rule asks for.
- **Why now is cheap:** no real data exists, AWS is paused, and every store
  already sits behind a port with an in-memory twin.
- **What it costs:** about 20 store adapters and 6,000 lines of DynamoDB code
  across `insolvia_core`, `api`, `admin`, `mcp` and `filing`, their tests,
  and a VPC the account has not had (about $9–10 a month per idle
  environment, mostly the NAT instance).
- **Rejected:** Aurora DSQL (no triggers, optimistic-commit retries
  everywhere, no local emulator); the RDS Data API (hand-mapped results, 1 MB
  responses); a publicly reachable database (unacceptable for GLBA-scope
  data). These were weighed for the dropped rewrite and still hold.

## Build breakdown

| # | Item | Depends on | Done when |
|---|---|---|---|
| 1 | **Foundation:** Postgres in compose, migration runner, `DATABASE_URL`, the real-Postgres store-test fixture, CI service container | — | `dev-up` starts a migrated Postgres; a trivial store test passes against it locally and in CI |
| 2 | **Firm stores:** firm, users, clients, library, checklist, questionnaire, client bindings, waitlist | 1 | Their store tests pass against Postgres; the DynamoDB adapters and the firms/waitlist tables are gone |
| 3 | **Case stores:** case, history, entities, debtors, candidates, tasks, requests, documents, events, jobs, packets | 1 | The same, for the cases table |
| 4 | **Filing and sealed data:** filing store, approvals, tax ids, credential vault, access log, admin audit, per-role grants and their test | 2, 3 | The grants test passes; an envelope sealed before the move still opens |
| 5 | **Local without AWS:** S3, queue and key stand-ins; seeder writes Postgres; `dev-up` needs only Cognito | 2, 3 | Sign in at `localhost:3000`, create a case, upload a document, build a packet, with no AWS session |
| 6 | **Hosting Terraform:** VPC, Aurora, migrator Lambda, IAM auth; KMS policy test; insolvia-services prerequisites PR | 1 | `terraform validate` and the policy test are green; nothing applied |
| 7 | **Sweep:** delete `adapters/aws/dynamo.py` and moto, update docs and skills | 2–6 | No DynamoDB left in code, infra or docs |

## Consequences

- Required checks keep their names; each service's PR job gains a Postgres
  service container.
- `docs/reference/{architecture,terraform,case-data-model}.md`,
  `scripts/README.md`, `seeds/README.md`, the services' `CLAUDE.md` files and
  the `insolvia-testing` and `insolvia-scripts` skills change as each store
  moves, in the PR that moves it.
- Aurora's 15-second resume after idle is accepted before launch; prod's
  minimum capacity is revisited at launch.
