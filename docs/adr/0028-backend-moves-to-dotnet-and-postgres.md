# ADR 0028 — The backend moves to .NET and PostgreSQL

- **Status:** Accepted 2026-10-27
  ([#511](https://github.com/insolvia-ai/insolvia/issues/511)). The
  maintainer decided scope, cut-over and hosting in chat (backend only; one
  big-bang cut-over; Aurora Serverless v2 in a VPC with a NAT instance), and
  accepted the delegated calls as written, including the Python freeze and
  re-baselining the PDFs. Not built: the build breakdown below is the work.
- **Date:** 2026-10-27
- **Supersedes:** plan decision **D6** ("Python + Flask + Mangum on Lambda")
  and [ADR 0012](0012-shared-python-domain-package.md). The
  shared-package *rule* carries over to .NET, and its admission rule
  stands.
- **Amends:** [ADR 0015](0015-async-pipelines-beside-the-lambdalith.md) (the
  lambdalith is ASP.NET Core, not Flask; image packaging stays),
  [ADR 0018](0018-sqs-queue-and-worker-lambda-over-step-functions.md) (the
  job record is a Postgres row, not a case-table item),
  [ADR 0021](0021-test-tiers-and-seed-fixtures.md) (the seed loader is .NET
  and reaches a private database through a Lambda),
  [ADR 0008](0008-testing-shape-follows-the-code-it-tests.md) (the services'
  pyramid is xUnit, and the data tier tests against a real Postgres),
  [ADR 0019](0019-ai-review-calls-anthropic-from-the-worker.md) (the call is
  made from the .NET SDK; its posture is unchanged).
- **Stands unchanged:** [0001](0001-client-stays-dumb-trust-boundary.md)
  (trust boundary), [0009](0009-a-case-belongs-to-a-firm.md) (firm
  tenancy), [0011](0011-cross-tenant-administration-is-a-separate-principal-class.md)
  (staff principal and audit), [0013](0013-mcp-server-replaces-direct-pms-integration.md)
  and [0016](0016-mcp-server-is-its-own-service.md) (MCP),
  [0014](0014-the-repository-is-the-regulatory-release-registry.md)
  (regulatory files ship in the artifact),
  [0023](0023-client-portal-identity-and-isolation.md) (portal) and
  [0024](0024-electronic-filing-path.md) (filing, vault isolation),
  [0026](0026-auth-is-a-shared-service.md) (Cognito read through SSM).

## Decision

**Everything in `services/` and `packages/insolvia_core` is rewritten in C#
on .NET 10 (LTS). Every DynamoDB table is replaced by one Aurora Serverless
v2 PostgreSQL database per environment, in a new VPC.** The HTTP contract
does not change. The Expo app, the admin SPA, marketing and
`packages/insolvia_api_client` are untouched, and that untouched state is
the acceptance test.

### Scope

- **Rewritten:** `services/api` (the API lambdalith and the job worker),
  `services/admin` (including the seeder and the purge CLI),
  `services/mcp`, `services/filing` and `packages/insolvia_core`.
- **Not rewritten:**
  - every TypeScript surface;
  - `forms/` (data plus a checker, already language-neutral);
  - `seeds/*.json` and the fixtures (language-neutral data);
  - `e2e/` (Playwright against deployed URLs);
  - S3, SQS, KMS and CloudTrail, which stay where they are. Only the
    tables move.

### Cut-over: one switch, built in the open

- **The .NET backend is built under a new top-level `backend/` directory,
  through ordinary PRs to `main`.** It deploys nowhere until the cut-over.
  This keeps the `main` ruleset and the one-PR-at-a-time discipline. It is
  "big bang" in the sense the maintainer chose: production changes once.
  It is not a months-long branch.
- **One switch PR (18.22)** repoints CI, infra and seeding at `backend/`.
  Staging and prod are **wiped and reseeded**, not migrated. The product is
  not live, and prod data is disposable before launch (maintainer,
  2026-09-26). A follow-up PR (18.23) deletes the Python.
- **The oracle is the running Python backend.** A base-URL contract suite
  (18.2) is made to pass against the local Python stack first, then runs
  against the .NET stack. The switch PR is blocked until both pass the same
  suite. The Python unit suites are the porting spec for each module. A
  ported module brings its tests across translated, not rewritten.

### Runtime shape

- **ASP.NET Core minimal APIs on Lambda**, through
  `Amazon.Lambda.AspNetCoreServer.Hosting`, still **image-packaged** (ADR
  0015).
- **The same deployable units as today:**
  - API, plus the job worker built from the same image family;
  - admin;
  - MCP;
  - filing worker.
- **New: `Insolvia.Migrator`**, a one-shot Lambda in the VPC that applies
  migrations and (in dev and staging) loads seeds. It exists because the
  database is private and a GitHub runner cannot reach it. CI invokes it
  before shifting the `live` alias.
- **Cold start is measured, not assumed.** The skeleton (18.1) reports p50
  and p95 cold start of the API image in a VPC. If p95 is above 3 s, the
  fallback is zip packaging with Lambda SnapStart. SnapStart is not
  available to image Lambdas, so this would amend ADR 0015's "every Lambda
  is image-packaged". Native AOT is not pursued: EF Core's AOT support is
  partial, and it is not worth constraining the port for.

### Data

- **Engine:** Aurora Serverless v2, PostgreSQL 17, minimum capacity 0
  (auto-pause), one cluster per environment, storage encrypted under a CMK.
  Prod's minimum capacity is revisited at launch: a 15-second resume on the
  first request after idle is acceptable before then, not after.
- **Access:** EF Core 10 with Npgsql for the stores. Plain SQL through
  Npgsql is used where a query reads better as SQL (cursors, the calendar
  range).
- **Schema:**
  - **Relational: one table per entity, replacing the single-table
    partitions.** The case partition's ~26 entity kinds become tables keyed
    by `(case_id, id)`. Firm entities become tables keyed by
    `(firm_id, id)`.
  - **Nested shapes are JSONB columns:** an entity's `body` and its
    `provenance` map. The domain treats them as documents and validates
    them in code. The confirm-before-entry and provenance invariants
    ([`case-data-model.md`](../reference/case-data-model.md)) stay in the
    domain layer, not in SQL.
  - Money is `numeric`, and every timestamp is `timestamptz`. Court dates
    are `date`, never converted to UTC.
  - **Tenancy is a column**: `firm_id` on every tenant row, with composite
    foreign keys so a case's children cannot point across firms.
    Row-level security is not adopted for v1. Authorization is still
    decided in the domain (ADR 0009).
- **Concurrency:**
  - Each conditional write the DynamoDB stores make (status CAS, the
    packet's `updatedAt` lock, approval single-use, never-twice filing
    transitions) becomes an `UPDATE … WHERE <condition>`. Zero rows
    affected maps to the same domain error as today's
    `ConditionalCheckFailed`.
  - Multi-item transactions (create a case with its history, bind a
    client, merge clients, swap the approval pointer) become one database
    transaction. The bind and the merge used to span two tables; now they
    don't.
- **Pagination:** index-bound cursors become keyset cursors. Cursors are
  opaque to clients today, so the wire shape survives.
- **Sealed data keeps its KMS envelopes byte-for-byte:**
  - The tax-id cipher and the filing-credential vault keep KMS
    `GenerateDataKey` with the same encryption contexts and the same
    AES-256-GCM envelope (`adapters/envelope.py`). Postgres stores the
    envelope as `bytea`; Postgres never sees plaintext.
  - The vault's isolation (ADR 0024) holds in two layers:
    - **KMS:** only the filing role can Decrypt, as today.
    - **Database roles:** the vault table grants `INSERT`/`DELETE` to the
      API role and `SELECT` to the filing role alone.
- **Append-only logs** (case access log, admin audit) are tables the
  application roles may only `INSERT` into, with no `UPDATE`, `DELETE` or
  `TRUNCATE`. They replace "no TTL, deletion-protected".
- **Database authentication is IAM, per service.** Each Lambda role maps to
  its own Postgres role: `api`, `worker`, `admin`, `mcp`, `filing`,
  `migrator`. The master credential is RDS-managed in Secrets Manager, and
  only the migrator and a human break-glass use it.
- **Purge and "scan all"** become plain SQL. The purge CLI keeps its
  dry-run default.

### Network

- **One VPC per deployed environment** (staging, prod):
  - two AZs;
  - private subnets for every backend Lambda and Aurora;
  - one public subnet holding a **fck-nat** instance (t4g.nano) for egress:
    Cognito and Google JWKS, the mailer, Anthropic, the CM/ECF courts.
- **Endpoints:** free gateway endpoints for S3 and DynamoDB. Every other
  AWS API goes through the NAT. Interface endpoints (about $7/month each)
  are added only when measured traffic says so.
- **One NAT instance is a single point of failure** for egress. That is
  accepted before launch. The trigger to move to a managed NAT gateway is
  the first real firm.

### Local, dev, staging, prod

| | Database | Backend runs as | How it is reached |
|---|---|---|---|
| **Local** | Postgres 17 in Docker (`backend/docker-compose.yml`), migrated on start | `dotnet run` or the compose stack, on the same ports as today (API 8080, admin 8090) | The shared dev Cognito pool via SSM. S3, SQS and KMS stay per-machine in `infra/envs/dev`. |
| **Tests** | Testcontainers Postgres, one per test run, on a laptop and on GitHub runners | xUnit, plus `WebApplicationFactory` for route tests | In-memory store twins for the domain and route tiers; real Postgres for the data tier |
| **Staging / prod** | Aurora Serverless v2 in the VPC | Image Lambdas in private subnets | The migrator Lambda runs before each alias shift |

**Local development gets better than today.** It used to talk to real
per-machine DynamoDB tables. Now the database runs offline. `infra/envs/dev`
loses its tables and keeps its bucket, queues and key.

### Libraries, chosen and spiked

- **PDF engine:**
  - The engine fills AcroForms and draws overlays and appearance streams.
    It is **PDFsharp 6.2.4, pinned exactly.** That is the last MIT
    release: 6.5.0 and later are AGPL-3.0-only or commercial (amended
    2026-10-27, spike 18.3), the same licence terms that rule out
    **iText**. Upgrading past 6.2.4 is therefore a licensing decision, not
    a dependency bump. PDFsharp 6.2.4 pulls in
    `System.Security.Cryptography.Pkcs` 8.0.1, which has a high-severity
    advisory, so it is referenced directly at a fixed version.
  - PDFsharp is used **only as a PDF object model**. The appearance
    streams come from a port of pypdf's generator. On all 22 forms, spike
    18.3 ([research note](../research/dotnet-pdf-engine-2026-10-27.md))
    measured:
    - every field and widget state equal;
    - **every text appearance stream byte-equal**;
    - **0.0000 % rendered-pixel difference**.
  - Whole files still aren't byte-identical (object order, `/ID`). So:
    - The sha256 goldens are re-baselined, as accepted.
    - Parity is gated by **field-level and appearance-level equivalence**
      plus the rendered-page diff, using the spike's `compare.py`, kept in
      CI until the cut-over.
    - Two pypdf quirks are reproduced on purpose, and 18.16 decides
      whether to keep or correct each: a button parent's `/V` written as a
      string, and shared appearance streams overwritten by the last widget.
  - The filing-set digest is defined over the .NET output from the
    cut-over on. There are no live approvals to invalidate.
- **MCP:** the official C# `ModelContextProtocol` SDK, if spike 18.4
  proves it serves the protocol revision the Python server pins today:
  stateless Streamable HTTP, JSON responses, RFC 9728 protected-resource
  metadata. If not, the fallback is a hand-written JSON-RPC endpoint (eight
  tools), pinned by the ported `test_protocol.py`.
- **Anthropic:** the official C# SDK, on the same model, structured output
  and document blocks (spike 18.4). ADR 0019's posture is unchanged.
- **JWT:** `Microsoft.IdentityModel.JsonWebTokens` with a cached JWKS. The
  fail-closed 401 matrix ports as tests.

### Repository layout

```
backend/
  Insolvia.slnx · Directory.Build.props · Directory.Packages.props · global.json
  src/  Insolvia.Core        domain + ports (the insolvia_core successor; no AWS, no EF)
        Insolvia.Engines     means test, CMI, exemptions, plan, deadlines (pure)
        Insolvia.Forms       projections + PDF engine (pure, embedded regulatory files)
        Insolvia.Data        EF Core, migrations, Postgres stores
        Insolvia.Aws         KMS envelope, S3, SQS, Cognito directory, mailer SigV4
        Insolvia.Api · Insolvia.Worker · Insolvia.Admin · Insolvia.Mcp · Insolvia.Filing · Insolvia.Migrator
  tests/ one project per src project; the architecture test pins the layering above
```

The layering rule of today's `test_architecture.py` ports as a NetArchTest
or reflection test: `Core` references nothing in the solution, and only
hosts reference `Data` or `Aws`. The filing worker uses the API's approval
and digest code through `Insolvia.Forms` and `Insolvia.Core`, not through
`Insolvia.Api`. That ends today's `ALLOWED_API` exception.

### Python in the meantime

**The Python backend is frozen to fixes from the day this is accepted.**
New backend features are built once, in `backend/`.
[ADR 0027](0027-a-cases-chapter-is-history.md) (the lifecycle history) is
therefore built directly into the new schema: its key change becomes a
table design instead of a reseed. The same holds for the backend half of
every data-model ticket filed but not started on 2026-10-27:
- [#501](https://github.com/insolvia-ai/insolvia/issues/501) — portal
  access keyed to the client;
- [#503](https://github.com/insolvia-ai/insolvia/issues/503) — one person
  in many firms;
- [#506](https://github.com/insolvia-ai/insolvia/issues/506) — the
  lifecycle history;
- [#507](https://github.com/insolvia-ai/insolvia/issues/507) — Chapter 13
  plan versions;
- [#509](https://github.com/insolvia-ai/insolvia/issues/509) — conversion
  and confirmation.

Each one's schema lands in 18.10 or 18.11, and its routes land in 18.14 or
18.15. The app halves of those tickets are unaffected. ADR 0024 PR 10
(per-court drivers) is also written against the .NET filing worker. A fix that does land in Python
after its module was ported carries a `backend-mirror` label and a mirror
issue.

## Context

- **Why the maintainer chose this:** the maintainer decided the stack, and
  this ADR records the decision; it does not argue it.
- **Why now is cheap:** the product is not live, prod data is disposable,
  and nothing has to be migrated.
- **What it costs:**
  - Roughly 70k lines of Python source and 57k lines of tests to port.
  - A VPC the account has never had.
  - Losing PyPDF byte-parity.
- **What drove the hosting choice:** the
  maintainer weighed the three no-VPC Postgres routes:
  - **Aurora DSQL:** no triggers or PL/pgSQL, optimistic-commit retries
    everywhere, no local emulator.
  - **The RDS Data API:** no EF Core, hand-mapped SQL, 1 MB responses.
  - **A publicly reachable database:** unacceptable for GLBA-scope data.

  The maintainer chose real Postgres in a VPC. The deciding argument is the
  repo's local-testability rule: a laptop's Docker Postgres *is* the
  production engine, while DSQL's limits would surface only after deploy.
- **Measured inputs** (the 2026-10-27 surveys):
  - 6 DynamoDB tables, no streams, TTL off everywhere it matters.
  - About 136 API routes, 7 admin routes, 8 MCP tools.
  - No OpenAPI document: `packages/insolvia_api_client`'s `index.test.ts`
    pins the wire by hand.
  - Every Lambda image-packaged, none in a VPC.

## Consequences

- **Account prerequisites land in insolvia-services first:**
  - ECR repositories for the new images;
  - the deploy role's EC2, RDS, Secrets Manager and `rds-db:connect`
    actions;
  - a human apply of `ci-trust`
    (`insolvia-deploy-role-permissions` skill). Its PR and release come
    before 18.5 can plan.
- **Required checks:** the PR job for `backend/` is `Backend (.NET)`, using
  `.github/actions/changed-paths` (no `paths:` filter, so it always
  reports). It becomes required when 18.1 lands. At the switch, the
  Python-named checks (`API service`, `Admin service`, `MCP service`,
  `Filing worker`, `Core package`) are removed through
  `scripts/update-ruleset.sh`, never by a hand-rolled PUT
  (`insolvia-branch-protection` skill).
- **Docs that change at the switch:**
  - `docs/reference/{architecture,terraform}.md`;
  - `infra/CLAUDE.md`;
  - `scripts/README.md`;
  - `seeds/README.md`;
  - the services' `CLAUDE.md` files, replaced by `backend/CLAUDE.md`;
  - the `insolvia-scripts`, `insolvia-testing` and `insolvia-deploy`
    skills.
- **Cost:** two NAT instances (about $6/month), Aurora storage and
  auto-paused ACUs (a few dollars a month per quiet environment), no
  NAT gateways.
- **Risk:** the port's volume, not its technology. The forms engine is the
  one place where technology is the risk, which is why its spike gates it.

## Build breakdown (milestone `Platform · .NET + Postgres`, plan 18.x)

| # | Item | Depends on | Done when |
|---|---|---|---|
| 18.1 | **Skeleton.** The `backend/` solution and layering test; `Backend (.NET)` CI; Dockerfile; compose with Postgres; `/health` on the Lambda host; dev-setup and pre-push wiring; cold-start measurement | — | CI is green; `docker compose up` serves `/health`; the pre-push hook runs `dotnet test`; the cold-start numbers are in the PR |
| 18.2 | **Contract suite.** A top-level `contract/` Vitest suite that calls a base URL **through `packages/insolvia_api_client` itself**, so it proves the shipped client, not a copy of it. Admin and MCP are covered over raw HTTP. It is TypeScript so that it outlives the Python | — | It runs with a base URL against the local Python stack, all green |
| 18.3 | **Spike: PDF engine** on PDFsharp | — | B101, B2010 and one radio form are field-equivalent to pypdf's output |
| 18.4 | **Spike: MCP and Anthropic C# SDKs** | — | A stateless MCP server answers the ported `test_protocol.py` cases; a structured-output call works |
| 18.5 | **Network and Aurora Terraform**, not wired to any Lambda, plus the insolvia-services prerequisites | the prerequisites | `Terraform validate` is green; the staging plan is additive only |
| 18.6 | **Core domain I:** firms, auth, access, errors, provenance, clients, ports | 18.1 | Their Python unit tests are ported and green |
| 18.7 | **Core domain II:** cases, debtors, entities, documents, candidates, tasks, requests, filings | 18.6 | The same |
| 18.8 | **Engines:** means test, CMI, exemptions, plan, deadlines, holidays, UST data | 18.7 — the engines read the case domain's types (debtors, income, claims, plans, assets) | Their tests are ported and green with Decimal parity |
| 18.9 | **Data foundation:** EF Core, migrations, lock/cursor helpers, Testcontainers, the migrator — infrastructure only, no domain tables | 18.1 | The migrator is idempotent; the helper tests pass against a real Postgres |
| 18.10 | **Firm stores** (firm, users, clients, library, checklist, questionnaire, bindings) | 18.7, 18.9 | The ported memory-twin conformance tests pass against Postgres |
| 18.11 | **Case stores** (case, history, lists, entities, debtors, candidates, tasks, documents, events, jobs) | 18.7, 18.9 | The same |
| 18.12 | **Sealed data:** KMS envelope, tax ids, vault, access log, audit, database roles and grants | 18.10, 18.11 | An envelope sealed by Python opens in .NET and vice versa; the grants test passes |
| 18.13 | **API host and auth** (JWT/JWKS, RequireAuth, Requires, RequireClient, CORS, error shape, logging) | 18.6 | The 401/403 matrix passes |
| 18.14 | **Firm-side routes** (me, firm, clients, library, questionnaire, tasks, notes, events, calendar) | 18.10, 18.13 | Their contract-suite slice is green against .NET |
| 18.15 | **Case routes** (cases, lifecycle including ADR 0027, debtors, entities, documents, requests, analyses) | 18.8, 18.11, 18.13 | The same |
| 18.16 | **Forms engine** (templates, 21 projections, fill/overlay, packets, filing set and digest) | 18.3, 18.8 | Field-equivalence holds for all 22 forms |
| 18.17 | **Job pipeline and worker** (packet assembly, AI review, extraction, extraction review) | 18.4, 18.15, 18.16 | A job runs end to end locally; the contract slice is green |
| 18.18 | **Portal** (identity, invitations, mailer client, portal routes) | 18.14, 18.15 | The contract slice is green |
| 18.19 | **Filing** (approval, credentials and authorization routes, filing worker, fence, fake CM/ECF, TOTP, receipt) | 18.12, 18.16 | The never-twice and fence tests pass against the fake court |
| 18.20 | **Admin service** (routes, audit, seeder, purge) | 18.12, 18.13 | The admin SPA works locally; the seeder writes nothing on a second run |
| 18.21 | **MCP service** | 18.4, 18.11, 18.13 | `test_protocol.py` is ported and green; the contract slice is green |
| 18.22 | **The switch:** CI, VPC wiring, migrator before alias, seeding, ruleset, docs; wipe and reseed staging, then prod | everything | A staging release is green end to end (contract, integration, e2e flows); prod is promoted by digest |
| 18.23 | **Delete the Python** backend and its CI | 18.22 in prod | No `services/` or `insolvia_core` left; the docs are swept |

## Accepted with

Both calls that were left open were accepted as written on 2026-10-27:
1. **Python takes fixes only.** The backend halves of #501, #503, #506, #507
   and #509 are built in `backend/`.
2. **The PDFs are re-baselined, not byte-matched.** Spike 18.3 then
   showed the fill reproduced field for field and pixel for pixel, so the
   re-baseline covers only the whole-file hashes.
