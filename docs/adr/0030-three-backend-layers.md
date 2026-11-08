# ADR 0030 — The backend is three layers, each project named for its role

- **Status:** Accepted 2026-11-08 — built in the same PR ([#604](https://github.com/insolvia-ai/insolvia/pull/604)).
- **Date:** 2026-11-08.
- **Relates to:** **Amends [ADR 0028](0028-backend-moves-to-dotnet-and-postgres.md)**,
  only its *Repository layout* section (which libraries exist, what they
  are called, and "one test project per src project"). Its hosts, its purity
  rule ("no AWS and no EF in the domain") and its filing-worker rule ("shared
  code never goes through `Api`") stand.
  [ADR 0016](0016-mcp-server-is-its-own-service.md) is why MCP stays a host
  of its own. [ADR 0029](0029-one-testing-shape.md) still decides which
  *level* owns a test; this ADR decides only which assembly it compiles into.

## Decision

**The backend is three layers: business logic, data access, and the API.**
The platform services the app runs on are a fourth, sibling project beside
data access. Each project is named for the role it plays, never for a vendor
and never with a word the product already uses for something else.

```
backend/src/
  Insolvia.Domain     business logic. Pure: entities, rules, ports; the
                      calculation engines and form projections (Engines/);
                      the PDF engine (Forms/). No cloud SDK, no EF, no Npgsql.
  Insolvia.Data       data access: EF Core, the migrations, the Postgres stores.
  Insolvia.Platform   platform services: document storage, sealing, the job
                      queues, the user directory, mail, sign-in keys.
  Insolvia.Api · Insolvia.Worker · Insolvia.Admin · Insolvia.Mcp ·
  Insolvia.Filing · Insolvia.Migrator     the API layer and the workers (hosts)
  Insolvia.UstTool    a developer tool, never deployed

backend/tests/
  one Insolvia.<Project>.Tests per project, except the migrator's tests,
  which live in Insolvia.Data.Tests/Migrator/ and share its Postgres container
```

That is 25 projects down to 19: three libraries where there were five, and
nine test projects where there were thirteen.

**The layering rule:**

| Layer | Project | May reference |
|---|---|---|
| business logic | `Insolvia.Domain` | nothing in the solution |
| data access | `Insolvia.Data` | `Domain` |
| platform services | `Insolvia.Platform` | `Domain` |
| API and workers | the hosts | `Domain`, `Data`, `Platform`; never another host |
| tools | `UstTool` | `Domain` only |

**Naming rules:**

- **A project is named for its role.** Not `Aws`: the project should not need
  renaming if the provider changes. Not `Integrations`: in this product an
  integration is a third-party system such as CM/ECF or a practice-management
  system, and those are not what this project holds. Not `Infrastructure`:
  here that word means Terraform (`infra/`). Not `Core`: it says the project
  is central, not what it holds, and invites a kitchen sink.
- **A vendor's name belongs on the class that implements a port**
  (`S3DocumentBlobStore`, `KmsCiphers`). That is where it is true, and where
  another provider's implementation would sit beside it while a host's wiring
  chooses between them. Domain's ports never name a vendor.
- **Namespaces match project and folder.** `src/Insolvia.Domain/Engines/` is
  `Insolvia.Domain.Engines.*`. A test namespace is the source namespace plus
  `.Tests` (`Insolvia.Domain.Engines.Tests`), so the code under test is in
  scope without a `using`. Embedded resources follow the namespaces
  (`Insolvia.Domain.Engines.Regulatory/…`). EF's migrations keep
  `Insolvia.Data.Migrations`, which is already right.

The layering test (`tests/Insolvia.Domain.Tests/Architecture`) pins all of
this. Domain is the one pure project. A `TestProjectOf` map names each source
project's test project, and every project must appear in the rule.

## Context

ADR 0028 ported the Python layout (`insolvia_core` plus one package per
concern) and kept five libraries: Core, Engines, Forms, Data and Aws. On
2026-11-08 the reference graph showed that three of those boundaries
restricted nothing:

- **Engines and Forms were only ever used together.** `UstTool` was the one
  project taking Engines without Forms, and Forms is pure too. The
  projections had already moved from Forms into Engines once (18.8, #519),
  because the engines and projections depend on each other. All three are
  business logic and belong in one assembly.
- **Data and Aws were referenced by every host together.** But merging them
  (this ADR's first draft did, as `Insolvia.Infrastructure`) lost something
  the maintainer wanted: a DLL that is only data access, so the layers read
  the way the code is reasoned about. The split is kept for clarity, not for
  restriction.
- **The AWS adapters are not data access.** Sealing, queues, the user
  directory and mail are services the app runs on. Folding them into Data
  would make "data access" mean "anything outside the process", so they get
  their own project, named for that role.

Each layer must stay its own project for the same reason the purity rule
exists: a project that does not reference EF Core or the AWS SDK cannot name
them, and the compiler enforces it.

The hosts stay separate because each is a separate deployable (its own image
stage, Lambda and IAM role). An ASP.NET host compiles a top-level `Program`,
and an assembly holds one. Host isolation is also a security boundary: MCP and
the filing worker never reach the API's internals.

The host *test* projects stay separate for a related reason. A test assembly
that references two hosts has two global `Program` types, and each extra one
needs an extern alias. `Api.Tests` already pays that once, for the Worker.

## Consequences

- **Admin, Mcp and Migrator now load the PDF engine** (PDFsharp and 4.5 MB
  of embedded templates) with Domain, though they never call it. Embedded
  resources are memory-mapped and read only on request, so the cost is disk,
  not cold start. If a measurement ever says otherwise, the fix is a
  resource-only assembly, not splitting the business logic layer again.
- **`internal` widens inside Domain.** The engines, the PDF engine and the
  domain types can see each other's internals. The old project-reference
  directions between them (Forms could use Engines, not the reverse) are not
  rebuilt with an analyzer. All of it is pure code, so a cycle there is a
  review problem, not a safety one.
- **Every namespace outside `Insolvia.Data` changed.** `Insolvia.Core` →
  `Insolvia.Domain`, `Insolvia.Engines` → `Insolvia.Domain.Engines`,
  `Insolvia.Forms` → `Insolvia.Domain.Forms`, `Insolvia.Aws` →
  `Insolvia.Platform`. It was a mechanical rename, checked by the compiler.
  Branches open at the time need a rebase, and git follows the file renames.
- **No infrastructure change.** Host assembly names, image stages,
  `CMD`/`ENTRYPOINT`, Lambda handlers and Terraform are unchanged, apart
  from comments that named a source path.
  - *Local:* `dotnet build`, `dotnet test`, `contract/scripts/dev-test.sh`
    and `docker compose build` are the checks, and all of them run offline.
  - *Staging and prod:* neither exists while AWS is paused
    ([runbook](../runbooks/aws-paused.md)). When they return, the images
    behave the same.
- **A new library concern is a folder in the layer it belongs to,** not a
  new project. A new project must be a new deployable or a new boundary the
  compiler has to enforce, and its PR says which.

## Alternatives considered

- **Keep the five libraries.** Rejected: Engines and Forms were business
  logic split three ways for no restriction, and `Aws` named a vendor.
- **Two libraries, `Core` and `Infrastructure`** (this ADR's first draft,
  built and then replaced before merge). Rejected: `Infrastructure` means
  Terraform here, `Core` says nothing about what it holds, and merging the
  data access layer with the platform services lost the data access DLL.
- **Data access absorbs the platform services** (classic three-tier: "the
  DAL is everything outside the process"). Rejected: sending mail and
  fetching sign-in keys are not data access, and the name would stop saying
  what the project holds.
- **`Insolvia.Integrations` for the platform services.** Rejected: that word
  is reserved for third-party systems the product integrates with.
- **One assembly for the hosts, with several Lambda handlers.** Rejected:
  one `Program` per assembly, per-host images and IAM roles, and host
  isolation as a security boundary.
- **One test project for all hosts.** Rejected because of the extern-alias
  cost described in *Context*.
