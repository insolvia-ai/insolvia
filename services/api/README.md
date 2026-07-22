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

Plain dev server (in-memory store; each submission is logged so local
marketing dev can see it):

```sh
python3 -m venv .venv && .venv/bin/pip install -r requirements-dev.txt
INSOLVIA_ENV=local PYTHONPATH=src .venv/bin/gunicorn --bind 127.0.0.1:8080 \
  insolvia_api.entrypoints.development_server:app
```

Full stack against dynamodb-local (real DynamoDB adapter, table auto-created):

```sh
docker compose up --build
# then inspect writes:
AWS_ACCESS_KEY_ID=local AWS_SECRET_ACCESS_KEY=local aws dynamodb scan \
  --endpoint-url http://127.0.0.1:8000 --table-name insolvia-waitlist-local
```

Checks: `ruff check .`, `ruff format --check .`, `pytest` (from this
directory; ruff config is the repo-root `ruff.toml`).

## Environment variables

| Variable | Meaning |
|---|---|
| `INSOLVIA_ENV` | `local` (default) \| `staging` \| `production`; also selects the CORS allowlist (`core/config.py`) |
| `WAITLIST_TABLE_NAME` | DynamoDB table for `POST /v1/waitlist`; required by the Lambda entrypoint, optional locally (unset → in-memory store) |
| `DYNAMODB_ENDPOINT_URL` | dynamodb-local override; **rejected outside `INSOLVIA_ENV=local`** |

CORS (issue #68) is an exact-origin allowlist — production:
`https://app.insolvia.ai`; staging: `https://staging-app.insolvia.ai` plus
localhost dev origins; local: localhost only. No wildcard: the desktop app
sends no `Origin` (CORS not in play), and `www.insolvia.ai` is absent on
purpose (its waitlist call is server-to-server). Logging (issue #69) is one
JSON line per request — metadata only, never bodies or PII (GLBA).
