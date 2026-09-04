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
| `FIRM_TABLE_NAME` | DynamoDB table holding firms, their people, and what each may do (`infra/modules/firm_store`). **Read on every authenticated request** — resolving the caller's firm is what makes a case reachable — so the Lambda entrypoint refuses to boot without it. Same local/in-memory shape as the others |
| `AUTH_ISSUER_URL` | Cognito OIDC issuer — `https://cognito-idp.<region>.amazonaws.com/<pool-id>`. Its `/.well-known/jwks.json` supplies the signing keys. Required by the Lambda entrypoint |
| `AUTH_USER_POOL_ID` | The pool this environment **calls** (`AdminCreateUser`, when a firm admin adds a colleague) — as against `AUTH_ISSUER_URL`, which it **verifies against**. Both end in the same id and neither is derived from the other: parsing an id out of a URL would make that URL's format load-bearing in a way Cognito never promised. Required by the Lambda entrypoint |
| `AUTH_CLIENT_ID` | The web app client id every access token must name (`client_id` claim). Required by the Lambda entrypoint |
| `ANTHROPIC_API_KEY` | The Anthropic API key the **worker's** AI petition review calls Claude with (issue #97, [ADR 0019](../../docs/adr/0019-ai-review-calls-anthropic-from-the-worker.md)). Deployed environments read SSM SecureString `/insolvia/<env>/api/anthropic-api-key` (human-created — see the ADR's rollout note); locally, add your own key to `services/api/.env` by hand. Unset → `petition_review` jobs fail deterministically with `not_configured`; nothing else is affected |

Both auth variables are published to SSM as `/insolvia/<env>/api/auth-issuer-url`
and `.../auth-client-id` and re-derived into the Lambda's environment by the
deploy workflow. **Unset never means "skip the check"**: `GET /v1/me` and every
future protected route answer 401 without them (`core/auth.py`), the same
fail-closed rule `UNSUBSCRIBE_SECRET` follows. Locally, `infra/envs/dev` outputs
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

## Authorization

Authentication answers *who*; a second step answers *what they may see*. A case
belongs to a **firm**, not to the person who opened it, so every case route
resolves the caller's firm user through the `by-subject` index on the firm
table (`current_accessor()`), and a signed-in caller who is **not in an active
firm answers 403** — not 401 (their token is fine, signing in again will not
help) and not 404 (it is a fact about their own account, with nothing to
enumerate).

`GET /v1/me` is the one route that resolves without requiring. It reports a
`firm` block, or omits it, so a user who has signed up and not yet been added
to a firm has something to render instead of an error. Its `permissions` are
the **effective** ones — a firm admin's stored map says
`firm_administration: hidden` and they can nonetheless manage users.

Which cases a caller sees depends on them: a firm admin, or anyone carrying
`accessAllCases`, lists their whole firm's; everyone else lists the matters
they are **linked** to. Those are two different indexes, so a pagination cursor
is only valid against the listing that minted it — flip a permission
mid-pagination and the next page is a 400 rather than a silent gap. The whole
visibility rule is `core/access.may_see_case`, deliberately in one place.

Per-feature permissions (`cases`, `intake`, `documents`, `extraction_review`,
`firm_administration`) sit on top as `@requires(FEATURE, LEVEL)`, below
`@require_auth`. They fail closed: a feature missing from a user's map, or a
level this version does not recognise, reads as `hidden`.

## Firm administration

`/v1/firm/users` (list, add, `PATCH`, `DELETE`) is the firm's own staff
management, gated on `firm_administration`. Adding a colleague mints their
Cognito account first — the pool has self-signup disabled — and the API holds
**`AdminCreateUser` and nothing else** on that pool: no password setting, no
delete, no auth. Cognito emails the temporary password to the invited address
and nothing here ever sees it, which is what keeps creating an account from
being a way to become one.

`/v1/firm/directory` is the thin one, gated on `cases` instead: subject,
display name, role. A case carries `createdBy` as a subject and an assignment
list is a list of subjects, so every member of a firm needs to turn one into a
name — and that need does not justify handing every paralegal their
colleagues' email addresses and permission maps.

**A firm cannot lock itself out.** Any edit that would leave it with no active
administrator answers 409, because self-signup is disabled and nobody inside
such a firm could appoint one. Unlinking the *last person from a case* is
allowed, by contrast — a case with nobody on it is still the firm's, and its
admins can always assign somebody new.

Assignment lives on the case (`PUT`/`DELETE /v1/cases/<id>/assignees/<subject>`)
and needs `cases: add_edit`, not `firm_administration`: putting a colleague on
a matter is case work, done by the attorney running it.

Routes are public unless decorated: `@require_auth` from `api/auth.py`, applied
**below** the route decorator. `/health`, `POST /v1/waitlist`, and
`POST /v1/unsubscribe` are public on purpose (see their docstrings). Every
rejection is a 401 with `{"error": "Unauthorized", "message": "authentication
required"}` — the reason stays in the log.
