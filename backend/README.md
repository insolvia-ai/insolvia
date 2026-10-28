# Insolvia backend (.NET)

The C# rewrite of Insolvia's backend: the API, job worker, admin service, MCP
server, filing worker and a migrator, on .NET 10 with PostgreSQL. It is being
built here in the open and replaces `services/` in one switch; until then it
deploys nowhere. The decision and the build plan are
[ADR 0028](../docs/adr/0028-backend-moves-to-dotnet-and-postgres.md).

## Prerequisites

- The .NET SDK that [`global.json`](global.json) names (10.0.302 or a later
  10.0 band). `./scripts/dev-setup.sh` at the repo root installs or checks it.
- Docker, for the compose stack and the images.

## Run

```bash
cd backend
dotnet test Insolvia.slnx
dotnet run --project src/Insolvia.Api      # http://127.0.0.1:8080/health
```

Or the whole local stack, Postgres included:

```bash
docker compose -f backend/docker-compose.yml up --build
curl http://127.0.0.1:8080/health
docker compose -f backend/docker-compose.yml down -v   # stop and wipe the database
```

Postgres listens on `127.0.0.1:55432` (user and database `insolvia`, password
`insolvia-local-only`, local only). Port 8080 is shared with the Python API,
so stop one before starting the other.

## Layout

```
Insolvia.slnx  global.json  Directory.Build.props  Directory.Packages.props
Dockerfile  docker-compose.yml
src/    Insolvia.Core · Engines · Forms · Data · Aws          libraries
        Insolvia.Api · Worker · Admin · Mcp · Filing · Migrator   hosts
tests/  one Insolvia.<Project>.Tests per project, plus Insolvia.Architecture.Tests
```

Only `Insolvia.Api` has code today: `GET /health`. The rest are empty
projects waiting for their port. [`CLAUDE.md`](CLAUDE.md) has the layering
rules.

## Images

`docker build --target lambda .` builds the API as AWS Lambda runs it (the
AWS .NET base image, behind an HTTP API); `--target development` builds the
same output on the ASP.NET Core runtime image for compose. The Lambda image
can be run locally through the Runtime Interface Emulator it ships with:

```bash
docker run --rm -p 9000:8080 <image>
curl -XPOST localhost:9000/2015-03-31/functions/function/invocations -d @event.json
```

where `event.json` is an API Gateway HTTP API (payload 2.0) event.
