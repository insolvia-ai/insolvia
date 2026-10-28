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
| `Insolvia.Engines`, `Insolvia.Forms` — pure | `Core` |
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

## Test tiers

- **Unit / route:** xUnit v3. Route tests run in-process through
  `WebApplicationFactory<Program>` (`tests/Insolvia.Api.Tests`).
- **Data:** a real Postgres through Testcontainers (arrives with 18.9) —
  never an in-memory EF provider.
- **Contract:** `contract/` at the repo root, against a running base URL.

## Run it

```bash
cd backend
dotnet build Insolvia.slnx                       # warnings are errors
dotnet test Insolvia.slnx                        # what the pre-push hook runs
dotnet format Insolvia.slnx                      # fix; CI runs --verify-no-changes
dotnet run --project src/Insolvia.Api            # http://127.0.0.1:8080/health
docker compose up --build                        # Postgres 17 on :55432 + the API on :8080
```

The API on 8080 is the same port as the Python API: run one at a time. The
CI check is `Backend (.NET)` in `.github/workflows/backend-pr.yml`;
`scripts/dev-test-unit.sh` maps any `backend/` change to the same format
and test steps.
