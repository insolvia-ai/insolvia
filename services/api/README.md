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

### `GET /v1/me`

Authenticated. The app's "is my token still good?" probe, and the identity it
displays. Answers from the claims this request's token already proved —
**there is no call back to Cognito**, no `GetUser`, no AWS call at all.

Responses:

- `200` — `{subject, username, clientId, scopes, expiresAt}`. `username` is the
  Cognito-generated UUID, **not** an email address, and clients must not
  display it as one; no address appears in any access-token claim (see
  [Authentication](#authentication)).
- `401` — as every protected route (see [Authentication](#authentication)).

### `POST /v1/unsubscribe`

Public (deliberately unauthenticated) like `POST /v1/waitlist`, and for the
same reason: the caller is the marketing site's SSR Lambda forwarding a click
server-to-server, and the person clicking holds no credentials. The token *is*
the authentication — an HMAC over the address signed with a secret only this
service holds — and verifying it is why this endpoint exists rather than the
marketing site calling the mailer directly.

Request body (JSON object, max 4 KiB):

| Field | Required |
|---|---|
| `token` | yes (a token this service minted) |

Responses:

- `202` — `{"status": "unsubscribed"}`, always exactly that. The response never
  reveals the address, never says whether it was already suppressed, and never
  says whether it matches a known account — anyone holding a link can call it.
- `400` — `{"error": "ValidationError", "message": "…"}` for a token that is
  invalid, forged or truncated, a missing `token`, a body that isn't a JSON
  object, or one over 4 KiB. Nothing is suppressed.
- `500` — no `UNSUBSCRIBE_SECRET` configured. Correct rather than unfortunate:
  without a key there is no way to tell a real token from a made-up one, and
  answering `202` would be a lie.

### Cases

Four routes backed by `core/cases.py` (the `case` root entity of [the case data
model](../../docs/reference/case-data-model.md)). All four are protected, so all
four can answer the `401` described under [Authentication](#authentication).
Three further things hold across them:

- **The owner comes from the verified token, never from the body.** There is no
  request a client can make that creates or reads a case belonging to someone
  else.
- **Someone else's case answers `404`, identically to one that does not
  exist.** A `403` would confirm the id is real, which turns these routes into
  an oracle for enumerating other firms' case ids — `core/errors.py`'s
  `NotFoundError` owns that reasoning.
- **`ownerPrincipal` is absent from every response.** The caller is the owner
  by construction, so returning it would leak a subject identifier for no
  purpose.

A case object is `{id, chapter, district, status, createdAt, updatedAt}`, with
`id`, `status`, `createdAt` and `updatedAt` all server-generated.

#### `POST /v1/cases`

Opens a case owned by the caller.

Request body (JSON object, max 64 KiB; unknown keys ignored):

| Field | Required | Accepted |
|---|---|---|
| `chapter` | yes | `7`, `11`, `12`, `13` (9 and 15 are municipal and cross-border — never this product) |
| `district` | yes | 2–64 characters of `A–Z a–z 0–9 space . - /` |

`status` is deliberately **not** accepted: every case starts at `intake`, and
letting a client create one already marked `filed` would be a lie the server
told on its behalf.

Responses:

- `201` — the case object.
- `400` — `{"error": "ValidationError", "fields": {"<field>": "<message>", …}}`
  for per-field failures, or `{"error": "ValidationError", "message": "…"}` when
  the body isn't a JSON object or exceeds 64 KiB.

#### `GET /v1/cases`

The caller's cases, newest first.

Query parameters: `limit` (1–100, default 50) and `cursor` (opaque, echoed from
a previous response — base64 rather than the raw key so clients cannot come to
depend on the table's attribute names).

Responses:

- `200` — `{"cases": [<case>, …]}`, plus `nextCursor` when there is another
  page. It is **absent rather than null** on the last page; the client contract
  distinguishes the two.
- `400` — a `limit` that isn't a number or falls outside 1–100, or a cursor this
  service did not mint.

Deliberately **not** written to the access log: that table is keyed by case, and
a list touches no case in particular. Recording enumeration properly wants a
by-principal index that `infra/modules/case_store` defers.

#### `GET /v1/cases/<case_id>`

Responses:

- `200` — the case object.
- `404` — `{"error": "NotFoundError", "message": "case not found"}`, for a case
  that does not exist and for one owned by someone else alike. The refused read
  *is* recorded in the access log: someone walking case ids is exactly what that
  log should show.

#### `PATCH /v1/cases/<case_id>`

Changes a case's chapter, district or status.

Request body (JSON object, max 64 KiB; unknown keys ignored) — any subset of:

| Field | Accepted |
|---|---|
| `chapter` | as `POST /v1/cases` |
| `district` | as `POST /v1/cases` |
| `status` | `intake`, `ready_to_file`, `filed` |

Only supplied keys are validated, so a caller changing the district alone is not
forced to resend the chapter.

Responses:

- `200` — the updated case object.
- `400` — per-field failures in the `fields` shape above, or a `message` body
  for an empty update. An empty body is **rejected rather than treated as a
  no-op**: it is far more likely a client bug than an intent, and a silent `200`
  would hide it.
- `404` — as `GET /v1/cases/<case_id>`, and likewise recorded.

## Local development

Local dev runs against **this machine's real AWS dev table** — there is no
local DynamoDB emulator. The per-machine layer
(`infra/envs/dev`: an isolated waitlist table + Cognito pool per developer
machine) is the dev database:

```sh
./scripts/dev-setup.sh                      # venv + per-machine AWS resources
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
| `CASE_TABLE_NAME` | DynamoDB table holding case data, encrypted under a customer-managed key. Same local shape as `WAITLIST_TABLE_NAME`; unset → in-memory store. Required by the Lambda entrypoint, in the pair below |
| `CASE_ACCESS_LOG_TABLE_NAME` | Append-only table recording who read or changed which case. The API's role holds `PutItem` on it and nothing else, so this service can write an entry and can never read, amend or delete one. Required by the Lambda entrypoint **together with** `CASE_TABLE_NAME` — one without the other would serve case data while recording nobody reading it |
| `MAILER_API_URL` | The mailer service's public HTTPS base URL; unset → in-memory mailer. Alone among these, that fallback survives into the Lambda: an environment whose mailer isn't deployed yet still boots |
| `UNSUBSCRIBE_SECRET` | HMAC key for unsubscribe tokens. Unset is **not** a fallback: `POST /v1/unsubscribe` answers 500 and outgoing mail carries no unsubscribe link, rather than degrading to unsigned tokens |
| `AUTH_ISSUER_URL` | Cognito OIDC issuer — `https://cognito-idp.<region>.amazonaws.com/<pool-id>`. Its `/.well-known/jwks.json` supplies the signing keys. Required by the Lambda entrypoint |
| `AUTH_CLIENT_ID` | The web app client id every access token must name (`client_id` claim). Required by the Lambda entrypoint |

The reasoning for each — which fall back, which fail closed, and why the case
pair is required as a pair — is `core/config.py`'s `load_config` docstring. This
table is the index; that docstring is the owner. Everything except
`WAITLIST_TABLE_NAME` (whose table sits in the same environment stack) is
published to SSM as `/insolvia/<env>/api/<kebab-cased-name>` and re-derived into
the Lambda's environment by the deploy workflow.

**For the auth pair, unset never means "skip the check"**: `GET /v1/me` and the
case routes answer 401 without them (`core/auth.py`), the same fail-closed rule
`UNSUBSCRIBE_SECRET` follows. Locally, `infra/envs/dev` outputs
`auth_issuer_url` and `auth_web_client_id` for this machine's own pool.

CORS (issue #68) is an exact-origin allowlist — production:
`https://app.insolvia.ai`; staging: `https://staging-app.insolvia.ai` plus
localhost dev origins; local: localhost only. No wildcard: the desktop app
sends no `Origin` (CORS not in play), and `www.insolvia.ai` is absent on
purpose (its waitlist call is server-to-server). Logging (issue #69) is one
JSON line per request — metadata only, never bodies or PII (GLBA).

## Authentication

The client sends the Cognito **access token** (not the ID token) as
`Authorization: Bearer <jwt>`. The service verifies the RS256 signature against
the pool's JWKS, then `iss`, `token_use == "access"`, `client_id`, and expiry —
Cognito access tokens carry `client_id` and no `aud`, so `aud` is deliberately
not checked. `GET /v1/me` answers from those verified claims alone; there is no
call back to Cognito, and no email (the pool's `username_attributes` is
`["email"]`, so an access token's `username` is a generated UUID — the app
displays the address from the ID token it already holds).

Routes are public unless decorated: `@require_auth` from `api/auth.py`, applied
**below** the route decorator. `/health`, `POST /v1/waitlist`, and
`POST /v1/unsubscribe` are public on purpose (see their docstrings). Every
rejection is a 401 with `{"error": "Unauthorized", "message": "authentication
required"}` — the reason stays in the log.
