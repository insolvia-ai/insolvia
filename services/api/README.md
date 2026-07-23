# services/api — the Insolvia backend API

Flask behind Mangum on Lambda (decision D6), layered `core` / `api` /
`adapters` / `entrypoints` with the dependency direction enforced by
`tests/test_architecture.py`. Every client capability is an API endpoint —
no client (web, desktop, or our own SSR Lambdas) ever talks to AWS data
stores directly. That trust boundary is
[ADR 0001](../../docs/adr/0001-client-stays-dumb-trust-boundary.md).

## Endpoints

### `GET /health`

Returns `200` with `{status, service, version, environment}`.

### `POST /v1/waitlist`

Public (deliberately unauthenticated) waitlist intake, called
server-to-server by the marketing site's SSR action. Abuse control is API
Gateway throttling (infra) plus the marketing form's honeypot — the honeypot
never reaches this API.

Request body (JSON object; unknown keys ignored; values trimmed):

| Field | Required | Max length |
|---|---|---|
| `name` | yes | 200 |
| `firm` | yes | 200 |
| `email` | yes (must look like an email) | 320 |
| `currentSoftware` | no | 100 |
| `message` | no | 2000 |
| `host` | no (the host that served the form) | 253 |

Responses:

- `201` — `{"id": "<uuid4>", "submittedAt": "<UTC ISO-8601, ms, Z>"}` (both
  server-generated).
- `400` — `{"error": "ValidationError", "fields": {"<field>": "<message>", …}}`
  for per-field failures, or `{"error": "ValidationError", "message": "…"}`
  when the body isn't a JSON object.

The stored DynamoDB item preserves the marketing implementation's schema
exactly (`PK="WAITLIST"`, `SK="<submittedAt>#<id>"`, optional fields omitted
rather than empty) — see `core/waitlist.py::record_item`.

## Local development

Local dev runs against **this machine's real AWS dev table** — there is no
local DynamoDB emulator (humbugg pattern). The per-machine layer
(`infra/envs/dev`: an isolated waitlist table + Cognito pool per developer
machine) is the dev database:

```sh
./scripts/dev-setup.sh --profile insolvia   # venv + per-machine AWS resources
./scripts/dev-up.sh                         # compose stack on :8080, real table
curl http://127.0.0.1:8080/health
```

`dev-up.sh` exports short-lived credentials from your AWS profile into the
container at `up` time and refuses to start until `dev-aws-setup.sh` has
written `services/api/.env` (see `scripts/README.md` at the repo root).

The bare dev server still runs with zero AWS — with `WAITLIST_TABLE_NAME`
unset it falls back to the in-memory store (each submission is logged so
local marketing dev can see it). That fallback is the unit-test seam, not the
dev path:

```sh
python3 -m venv .venv && .venv/bin/pip install -r requirements-dev.txt
INSOLVIA_ENV=local PYTHONPATH=src .venv/bin/gunicorn --bind 127.0.0.1:8080 \
  insolvia_api.entrypoints.development_server:app
```

Checks: `ruff check .`, `ruff format --check .`, `pytest` (from this
directory; ruff config is the repo-root `ruff.toml`).

## Environment variables

| Variable | Meaning |
|---|---|
| `INSOLVIA_ENV` | `local` (default) \| `staging` \| `production`; also selects the CORS allowlist (`core/config.py`) |
| `WAITLIST_TABLE_NAME` | DynamoDB table for `POST /v1/waitlist`; required by the Lambda entrypoint. Locally it names this machine's real dev table (written to `.env` by `dev-aws-setup.sh`); unset → in-memory store (test seam) |

CORS (issue #68) is an exact-origin allowlist — production:
`https://app.insolvia.ai`; staging: `https://staging-app.insolvia.ai` plus
localhost dev origins; local: localhost only. No wildcard: the desktop app
sends no `Origin` (CORS not in play), and `www.insolvia.ai` is absent on
purpose (its waitlist call is server-to-server). Logging (issue #69) is one
JSON line per request — metadata only, never bodies or PII (GLBA).
