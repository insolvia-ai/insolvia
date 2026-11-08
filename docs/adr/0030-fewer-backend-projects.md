# ADR 0030 — The backend keeps one project per deployable and one per purity boundary, not one per folder

- **Status:** Accepted 2026-11-08 — built in the same PR ([#604](https://github.com/insolvia-ai/insolvia/pull/604)).
- **Date:** 2026-11-08.
- **Relates to:** **Amends [ADR 0028](0028-backend-moves-to-dotnet-and-postgres.md)**,
  only its *Repository layout* section (the five libraries and "one test
  project per src project"). Its hosts, its purity rule ("no AWS and no EF in
  the domain") and its filing-worker rule ("shared code never goes through
  `Api`") stay as they are.
  [ADR 0016](0016-mcp-server-is-its-own-service.md) is why MCP stays a host
  of its own. [ADR 0029](0029-one-testing-shape.md) still decides which
  *level* owns a test. This ADR only decides which *assembly* the test is
  compiled into.

## Decision

**A backend project exists for one of two reasons: it is a separate
deployable, or it marks a boundary the compiler has to enforce.** Today's
layout has twelve source projects and thirteen test projects. Three of the
library splits and five of the test splits meet neither reason. They merge:

```
backend/src/
  Insolvia.Core            was Core + Engines + Forms. Pure: no AWS, no EF, no Npgsql.
  Insolvia.Infrastructure  was Data + Aws. The adapters behind Core's ports.
  Insolvia.Api · Insolvia.Worker · Insolvia.Admin · Insolvia.Mcp · Insolvia.Filing · Insolvia.Migrator   unchanged
  Insolvia.UstTool         unchanged; it references Core only

backend/tests/
  Insolvia.Core.Tests            was Core.Tests + Engines.Tests + Forms.Tests + Architecture.Tests
  Insolvia.Infrastructure.Tests  was Data.Tests + Aws.Tests + Migrator.Tests: one Postgres container, not two
  Insolvia.Api.Tests · Insolvia.Worker.Tests · Insolvia.Admin.Tests · Insolvia.Mcp.Tests · Insolvia.Filing.Tests · Insolvia.UstTool.Tests   unchanged
```

That is 25 projects down to 17.

**Namespaces do not change.** `Insolvia.Engines.MeansTest`,
`Insolvia.Forms.Pdf`, `Insolvia.Data.Stores`, `Insolvia.Aws.Kms` and every
other namespace keep their names inside the merged assemblies. Every
backend `.cs` file declares its namespace explicitly, so a merge is a
`git mv` of the folder plus edits to `.csproj`, `.slnx` and the Dockerfile.
No `using` line changes. Embedded resources keep their `LogicalName`s
(`Insolvia.Engines.Regulatory/…`, `Insolvia.Forms.Templates/…`), so
resource lookups don't change either. EF migrations keep the
`Insolvia.Data.Migrations` namespace and their ids, so
`__EFMigrationsHistory` sees no difference.

**The layering rule after the merge:**

| Project | May reference |
|---|---|
| `Insolvia.Core`: domain, ports, engines, projections, PDF engine | nothing in the solution; no `AWSSDK.*`, `Amazon.*`, EF Core or Npgsql package |
| `Insolvia.Infrastructure`: EF Core and Postgres stores, migrations, AWS SDK adapters | `Core` |
| hosts: `Api`, `Worker`, `Admin`, `Mcp`, `Filing`, `Migrator` | `Core`, `Infrastructure`; **never another host** |
| tools: `UstTool` | `Core` only |

`Insolvia.Architecture.Tests`' checks move into `Insolvia.Core.Tests`
under `Architecture/`, with these changes:
- the purity check (`The_domain_takes_no_cloud_or_database_package`) now
  covers one project instead of three;
- "only hosts reference Data or Aws" becomes "only hosts reference
  Infrastructure";
- "every source project has its own test project" becomes "every source
  project is named in a test map". The map is a dictionary in the test,
  and every test project in it must exist.

## Context

ADR 0028 ported the Python layout (`insolvia_core` plus one package per
concern) and the textbook clean-architecture split together. Nobody checked
whether .NET needed each split. On 2026-11-08 the reference graph showed:

- **Every host references both `Data` and `Aws`.** Api, Worker, Admin, Mcp,
  Filing and Migrator all reference both, and `Data` already carries an AWS
  package (`AWSSDK.RDS`, for IAM database tokens). So the split restricts
  nothing: no host sees one without the other.
- **Only `UstTool` uses `Engines` without `Forms`**, and `Forms` is just as
  pure, so taking it costs `UstTool` nothing. The projections already had to
  move from Forms into Engines once (18.8, #519), because the engines and the
  projections depend on each other.
- **The `Core` / `Engines` split** keeps calculation code away from Admin,
  Mcp and Migrator. That is tidy, but those hosts can't do harm with code
  that has no I/O.
- **Compile-time enforcement needs only one boundary.** It is the purity
  rule: the domain must not be able to name EF Core or the AWS SDK. A
  project that doesn't reference those packages can't name them, and the
  architecture test stops anyone adding them. Two projects, pure and
  adapters, give exactly that.
- **Data.Tests and Migrator.Tests each start a Postgres container.** One
  container per assembly is the rule, and Migrator.Tests already compiles
  Data.Tests' `PostgresFixture.cs` in by link. Merging them means one
  container per run.

The hosts are a different case, and they stay separate:
- each is its own image stage in `backend/Dockerfile`, its own Terraform
  module, its own Lambda and its own IAM role;
- an ASP.NET host compiles a top-level `Program`, and one assembly can hold
  only one;
- the rule that a host never references another host is a security
  boundary: it stops MCP or the filing worker calling the API's internals.

The host *test* projects stay separate for a related reason. A test
assembly that references two hosts has two global `Program` types, and
`WebApplicationFactory<Program>` needs an extern alias for each extra one.
`Api.Tests` already pays that once for the Worker
(`PipelineEndToEndTests.cs`). Folding all six together would mean five
aliases and a `Program` that no reader can tell apart.

## Consequences

- **Admin, Mcp and Migrator images gain the PDF engine.** They now take
  PDFsharp and 4.5 MB of embedded form templates they never call. Embedded
  resources are memory-mapped and read only when asked for, so there's no
  cold-start cost beyond a larger file on disk. This is accepted. If a
  measured cold start ever says otherwise, the fix is to split the templates
  into a resource-only assembly, not to restore three libraries.
- **`internal` widens inside Core.** An `internal` type in the engines
  becomes visible to the PDF engine and the domain, and the same goes for
  `Data` and `Aws` inside `Infrastructure`. The namespace-direction rules
  that project references used to enforce are dropped instead of being
  rebuilt with NetArchTest. Engines → Forms (Forms may use Engines, not the
  reverse) and Core → Engines are the two that go. Everything on both sides
  is pure, and a cycle between pure namespaces is a readability problem for
  code review, not a safety problem for a test.
- **No infrastructure changes.** Host assembly names, image stages, `CMD`
  and `ENTRYPOINT` values, Lambda handlers and Terraform modules all stay
  the same.
  - *Local:* `dotnet build`, `dotnet test`, `contract/scripts/dev-test.sh`
    and `docker compose up` are the checks for each step, and all of them
    run offline.
  - *Staging and prod:* neither exists while AWS is paused
    ([runbook](../runbooks/aws-paused.md)). When they come back, the images
    behave the same and only their layers differ.
- **The EF command changes** to `--project src/Insolvia.Infrastructure`.
  `backend/CLAUDE.md` owns that line and is updated in the same PR.
- **Docs that name the old projects change with the code:**
  `backend/CLAUDE.md` (the layering table), `backend/README.md`, the
  `insolvia-testing` and `insolvia-new-package` skills (they name
  `Insolvia.Architecture.Tests`) and the Dockerfile's restore `COPY` lines.
  ADR 0028's layout block keeps its text and gets an "amended by 0030" note.
- **It lands as two commits, each green on its own.** Each one reports the
  test count before and after, and the counts must match:
  1. `Core` absorbs `Engines` and `Forms`. `Core.Tests` absorbs their tests
     and `Architecture.Tests`. `UstTool` drops its `Engines` reference.
  2. `Data` and `Aws` become `Infrastructure`. `Infrastructure.Tests`
     absorbs `Data.Tests`, `Aws.Tests` and `Migrator.Tests`.
- **A new library project now needs a reason.** It must be a new
  deployable or a new boundary the compiler has to enforce, and the PR has
  to say which. "It's a different concern" is answered by a folder and a
  namespace.

## Alternatives considered

- **Keep the layout.** It works, and the architecture test holds it in
  place. Rejected because three of the five library boundaries restrict
  nothing that any reference actually uses. Each one costs a `.csproj`, a
  lockfile, a Dockerfile `COPY` line, a test project and a line in the
  layering rule, and contributors pay that every time they decide where code
  goes.
- **One library: Core, Engines, Forms, Data and Aws together.** Rejected:
  this is the one split that earns its place. With EF Core and the AWS SDK
  in the same assembly as the engines, the purity rule could only be
  enforced by convention.
- **Merge, then rebuild the old namespace directions with NetArchTest.**
  Rejected: it adds a package to guard a separation between pure code that
  nobody relies on. If one of those directions turns out to matter, put it
  back in a test then, with the reason written down.
- **Merge the hosts into one assembly with several Lambda handlers.**
  Rejected for the three reasons in *Context*: one `Program` per assembly,
  per-host images and IAM roles, and host isolation as a security boundary.
- **One test project for all hosts.** Rejected because of the extern-alias
  cost described in *Context*. The one existing pairing (Api.Tests
  referencing the Worker for the end-to-end pipeline test) stays as it is.
