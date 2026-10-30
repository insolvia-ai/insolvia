# backend/ — agent rules

The .NET 10 + PostgreSQL rewrite of `services/` and `packages/insolvia_core`
([ADR 0028](../docs/adr/0028-backend-moves-to-dotnet-and-postgres.md); plan
18.x). It deploys **nowhere** until the switch PR (18.22); until then the
Python services are what runs, and they are the oracle: a ported module
brings its Python tests across, translated, not rewritten.

## Layering — pinned by `tests/Insolvia.Architecture.Tests`

| Project | May reference |
|---|---|
| `Insolvia.Core` — domain + ports | nothing in the solution |
| `Insolvia.Engines` — pure: calculation engines and form projections | `Core` |
| `Insolvia.Forms` — pure: the PDF engine, which fills what Engines projects | `Core`, `Engines` |
| `Insolvia.Data` (EF Core, Postgres), `Insolvia.Aws` (SDK adapters) | `Core` |
| hosts: `Api`, `Worker`, `Admin`, `Mcp`, `Filing`, `Migrator` | any library above; **never another host** |

- **No AWS and no EF in Core, Engines or Forms.** A port (interface) lives in
  Core; its adapter lives in `Data` or `Aws`; a host wires them. The
  architecture test also refuses `AWSSDK.*`, `Amazon.*`, EF Core and Npgsql
  packages in those three.
- Code the filing worker shares with the API (approval, digest) goes in
  `Forms` or `Core`, never in `Api`.
- A new project needs a `tests/<Name>.Tests` project and a line in
  `Insolvia.slnx`, and the rule above must name it. The test fails until all
  three are done.

## Rules that are easy to get wrong

- **Money is `decimal`**, never `double`; Postgres `numeric`. Engine ports
  must hold Decimal parity with the Python.
- Timestamps are `DateTimeOffset` / `timestamptz`; court dates are `DateOnly`
  / `date`, never converted to UTC.
- Package versions live in `Directory.Packages.props` only. Every project has
  a committed `packages.lock.json`; CI and the Dockerfile restore with
  `--locked-mode`, so commit the lock diff with the change.
- Warnings are errors (`Directory.Build.props`). Do not add `NoWarn` to make a
  build pass without saying why on the same line.
- The wire contract is the Python API's, byte for byte where a test can see
  it (`/health` keeps Flask's sorted keys and bare `application/json`).
- One deployable unit, one Dockerfile target. A host gets its stage when it
  gets a handler, and `backend-pr.yml` builds it in the same PR.
- `spikes/` holds standalone throwaway projects, deliberately **not** in the
  solution.

## The data tier — `Insolvia.Data` and `Insolvia.Migrator`

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

## Test tiers

- **Unit / route:** xUnit v3. Route tests run in-process through
  `WebApplicationFactory<Program>` (`tests/Insolvia.Api.Tests`).
- **Data:** a real Postgres 17 through Testcontainers — never an in-memory
  EF provider. One container per test assembly (`PostgresFixture`, a
  collection fixture), a fresh database per test (`CreateDatabaseAsync`).
  Needs Docker locally; GitHub's ubuntu runners have it.
- **Contract:** `contract/` at the repo root, against a running base URL.

## Run it

```bash
cd backend
dotnet build Insolvia.slnx                       # warnings are errors
dotnet test Insolvia.slnx                        # what the pre-push hook runs
dotnet format Insolvia.slnx                      # fix; CI runs --verify-no-changes
dotnet run --project src/Insolvia.Api            # http://127.0.0.1:8080/health
dotnet run --project src/Insolvia.Worker         # the job poller: JOB_QUEUE_URL, INSOLVIA_DB_*, CASE_DOCUMENT_BUCKET (+ ANTHROPIC_API_KEY)
docker compose up --build                        # Postgres 17 on :55432, the migrator, then the API on :8080
INSOLVIA_DB_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Database=insolvia;Username=insolvia;Password=insolvia-local-only' \
INSOLVIA_DB_ROLE_PASSWORD=insolvia-local-only \
  dotnet run --project src/Insolvia.Migrator      # migrate the compose database by hand
```

The API on 8080 is the same port as the Python API: run one at a time. The
CI check is `Backend (.NET)` in `.github/workflows/backend-pr.yml`;
`scripts/dev-test-unit.sh` maps any `backend/` change to the same format
and test steps.
