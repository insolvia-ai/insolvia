# backend/ — agent rules

Insolvia's backend: .NET 10 on Lambda, PostgreSQL (Aurora in staging and
prod, Docker locally) —
[ADR 0028](../docs/adr/0028-backend-moves-to-dotnet-and-postgres.md). It
replaced the Python services and `insolvia_core` at the switch (18.22), and
the Python was deleted in 18.23. Comments that name a Python module
(`core/x.py`, `services/api/...`) record where a port came from; the module
itself is gone, and `git log` before 18.23 has it.

## Layering — pinned by `tests/Insolvia.Domain.Tests/Architecture`

Three layers ([ADR 0030](../docs/adr/0030-three-backend-layers.md)):

| Layer | Project | May reference |
|---|---|---|
| business logic | `Insolvia.Domain` — pure: entities, rules and ports; the calculation engines and form projections (`Engines/`); the PDF engine (`Forms/`) | nothing in the solution |
| data access | `Insolvia.Data` — EF Core, the migrations, the Postgres stores | `Domain` |
| platform services | `Insolvia.Platform` — document storage, sealing, the job queues, the user directory, mail, sign-in keys | `Domain` |
| API (and the workers) | hosts: `Api`, `Worker`, `Admin`, `Mcp`, `Filing`, `Migrator` | `Domain`, `Data`, `Platform`; **never another host** |
| — | tools: `UstTool` — run by a developer or CI, never deployed (no Dockerfile stage) | `Domain`; never `Data`, `Platform` or a host |

- **Name a project for its role, never for a vendor or a product word.** No
  `Aws`, no `Integrations` (that word means third-party systems such as
  CM/ECF), no `Core`/`Common`. A provider's name belongs on the class that
  implements a port (`S3DocumentBlobStore`, `KmsCiphers`), where a second
  provider's class would sit beside it.
- **Namespaces match the project and folder**: `src/Insolvia.Domain/Engines/`
  is `Insolvia.Domain.Engines.*`, and a test project's namespaces are the
  source namespace plus `.Tests` (`Insolvia.Domain.Engines.Tests`), so the
  code under test is in scope without a `using`.
- **No cloud SDK and no EF in Domain.** A port (interface) lives in Domain;
  its implementation lives in `Data` or `Platform`; a host wires them. The
  architecture test also refuses `AWSSDK.*`, `Amazon.*`, EF Core and Npgsql
  packages in Domain.
- A new concern is a folder and a namespace in the layer it belongs to, not a
  new project. A new project must be a new deployable or a boundary the
  compiler has to enforce; say in the PR which.
- Code the filing worker shares with the API (approval, digest) goes in
  `Domain`, never in `Api`.
- A new project needs a test project (`TestProjectOf` in the layering test
  maps each project to one), a line in `Insolvia.slnx`, and the rule above
  must name it. The test fails until all three are done.

## Rules that are easy to get wrong

- **Money is `decimal`**, never `double`; Postgres `numeric`. The engines'
  parity outputs (`tests/Insolvia.Domain.Tests/Engines/Parity/*.expected.json`,
  captured from the Python oracle) pin Decimal-exact results; a deliberate
  change edits them in the same PR.
- Timestamps are `DateTimeOffset` / `timestamptz`; court dates are `DateOnly`
  / `date`, never converted to UTC.
- Package versions live in `Directory.Packages.props` only. Every project has
  a committed `packages.lock.json`; CI and the Dockerfile restore with
  `--locked-mode`, so commit the lock diff with the change.
- Warnings are errors (`Directory.Build.props`). Do not add `NoWarn` to make a
  build pass without saying why on the same line.
- The wire contract is the one the Python API defined, byte for byte where a
  test can see it (`/health` keeps Flask's sorted keys and bare
  `application/json`): `contract/` and `packages/insolvia_api_client` pin it,
  and the app depends on it.
- **A route change edits its OpenAPI row** in `tool/openapi/api-routes.ts`
  or `admin-routes.ts`, then `npm run openapi`
  ([`docs/reference/openapi/`](../docs/reference/openapi/README.md)).
  `OpenApiDocumentTests` fails on a route, principal or permission the
  document does not match. `Requires(...)` stamps a `PermissionRequirement`
  on the endpoint for that test to read.
- One deployable unit, one Dockerfile target. A host gets its stage when it
  gets a handler, and `backend-pr.yml` builds it in the same PR.

## The data access layer — `Insolvia.Data` and `Insolvia.Migrator`

- **One context, `InsolviaDbContext`, one schema, `insolvia`.** An entity
  joins the model through an `IEntityTypeConfiguration<T>` in `Insolvia.Data`
  (picked up by assembly scan); nobody edits the context to add a table. Its
  conventions are the rules above made mechanical: snake_case names,
  `DateTimeOffset` → `timestamptz` (written as UTC), `decimal` → `numeric`,
  `DateOnly` → `date`, `JsonDocument` → `jsonb` (an entity's `body` /
  `provenance`). A `DateTime` property fails the model build.
- **Adding a migration:** change the model, then
  ```bash
  cd backend && dotnet tool restore
  dotnet ef migrations add <PascalCaseName> --project src/Insolvia.Data --startup-project src/Insolvia.Migrator --output-dir Migrations
  ```
  and commit the migration, its `.Designer.cs` and the updated snapshot
  together. `ModelTests.The_real_model_has_no_change_that_a_migration_does_not_capture`
  fails CI when a model change has no migration.
- **Never edit an applied migration** — one that has reached `main`. Write a
  new one. Staging and prod are wiped and reseeded before launch (ADR 0028),
  which is not a licence to rewrite history: a developer's database and every
  test run replay the chain as committed.
- **Conditional writes** (`Concurrency/ConditionalWrite`): the precondition
  goes in the statement's `WHERE`, and zero rows affected throws the caller's
  domain error — the Python store's `ConditionalCheckFailed` translation.
  Never read-then-write. A once-only insert is `SaveChangesOrConflictAsync`.
  A multi-row change is `InTransactionAsync`.
- **Every write must be safe to retry** (#567). `UseInsolviaNpgsql` turns on
  EF's retrying strategy (6 retries, ≤ 5 s apart — `InsolviaNpgsql.MaxRetryCount`
  says why: a paused Aurora resuming inside a 30 s request). EF replays
  queries, `SaveChanges`, `ExecuteUpdate`/`ExecuteDelete` and each
  `InTransactionAsync` unit whole; it does **not** replay `ExecuteSql…` outside
  a transaction (use `RetrySafety.ExecuteIdempotentSqlAsync` for an upsert) or
  anything on a raw `NpgsqlConnection` (the migrator's `RoleProvisioner` and
  `PreClientPurge`). A transaction begun outside `InTransactionAsync` is
  refused by EF. For each new write:
  - an `InTransactionAsync` delegate does its reads **inside** and is safe to
    run twice from the start;
  - assume the **lost acknowledgement**: the commit landed, the reply did not,
    and the write runs again on top of itself. Idempotent (an unconditional
    `SET` of fixed values, an upsert) needs nothing. A conditional write, a
    once-only insert or a delete would answer the caller "refused" / "already
    exists" / "not found" for its own write — give it a `landed` check that
    recognises *this call's* write (`InTransactionAsync(…, landed:)`,
    `RetrySafety.WriteOnceAsync`, `DeleteOnceAsync`). Compare on something only
    this call wrote — a generated id, the attempt id, the `updated_at` it set —
    never just "a row like it", or a rival's write reads as yours (a filing
    claimed twice).
  - `InsertOnceAsync` handles its own: a generated key's conflict on a replay
    counts as done (no read — the waitlist and admin audit roles cannot read
    their tables); a key callers compete for passes `alsoMatching:`.
  - `RetryTests` (Data.Tests) injects both faults through `FaultInjector`;
    a new write with a `landed` check gets a lost-ack test there.
- **Listings page by keyset** (`Pagination/`): a `Keyset<TRow, TKey>` with a
  total order and a name that includes everything that changes the rows
  (index, view). `CursorCodec` binds a cursor to that name and refuses edits;
  a store maps `InvalidCursorException` to the domain's validation error.
- **Roles and grants live in the migrator's table**
  (`src/Insolvia.Migrator/Grants.cs`, `GrantTable.All`) — never in a
  migration. A store that adds a table adds its rows there in the same PR; a
  table no row names is reachable by no service. An append-only log is
  `TableGrant.AppendOnly(table, role)`. The migrator re-asserts the whole
  table on every run, so removing a row revokes the grant.
- **Who connects as whom:** each service as its own role (`api`, `worker`,
  `admin`, `mcp`, `filing`) — locally with a password, deployed with an IAM
  token (`INSOLVIA_DB_*`, `DatabaseSettings`). Only the migrator connects as
  the superuser / RDS master, because only it creates roles.

## Test levels

The levels and who owns what: [ADR 0029](../docs/adr/0029-one-testing-shape.md);
how to write each: the `insolvia-testing` skill.

- **Unit / route:** xUnit v3. Route tests run in-process through
  `WebApplicationFactory<Program>` (`tests/Insolvia.Api.Tests`). They own
  every **refusal** — the auth matrix, validation, error mapping, edge cases,
  host seams; `contract/` owns the happy path, the wire shape and tenancy
  (ADR 0029 rule 1). An assertion goes to one of them, not both.
- **Data:** a real Postgres 17 through Testcontainers — never an in-memory
  EF provider. One container per test assembly (`PostgresFixture`, a
  collection fixture), a fresh database per test (`CreateDatabaseAsync`).
  Needs Docker locally; GitHub's ubuntu runners have it.
- **Contract:** `contract/` at the repo root, against the hosts on a
  throwaway Postgres (`contract/scripts/dev-test.sh`) — and its deployed
  target, signed in through the real pool, against staging after each deploy
  or a laptop's compose stack (`contract/scripts/dev-test.sh --target deployed`).

## Run it

```bash
cd backend
dotnet build Insolvia.slnx                       # warnings are errors
dotnet test Insolvia.slnx                        # what the pre-push hook runs
dotnet format Insolvia.slnx                      # fix; CI runs --verify-no-changes
dotnet run --project src/Insolvia.Api            # http://127.0.0.1:8080/health
dotnet run --project src/Insolvia.Worker         # the job poller: JOB_QUEUE_URL, INSOLVIA_DB_*, CASE_DOCUMENT_BUCKET (+ ANTHROPIC_API_KEY)
./scripts/dev-up.sh                              # this compose stack (backend/scripts/dev-up.sh) beside the apps
docker compose up --build                        # Postgres 17 on :55432, the migrator, then the API on :8080, admin on :8090
INSOLVIA_DB_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Database=insolvia;Username=insolvia;Password=insolvia-local-only' \
INSOLVIA_DB_ROLE_PASSWORD=insolvia-local-only \
  dotnet run --project src/Insolvia.Migrator      # migrate the compose database by hand
```

Compose reads `backend/.env` (`scripts/dev-aws-setup.sh` writes it: the
shared dev pool's ids, so the app can sign in; `INSOLVIA_SEED=false`, because
`scripts/dev-aws-seed.sh` then seeds with the pool's real subjects). The CI
check is `Backend (.NET)` in `.github/workflows/backend-pr.yml`;
`scripts/dev-test-unit.sh` maps any `backend/` change to the same format and
test steps.
