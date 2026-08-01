# services/api — agent rules

Flask + Mangum on Lambda. Human docs: [`README.md`](README.md). Run with
`scripts/dev-up.sh`; test gate `scripts/dev-test.sh` (ruff + pytest, exactly as CI).

- **Layered `core / api / adapters / entrypoints`** with the dependency
  direction enforced by `tests/test_architecture.py`: `core` depends on nothing
  else; `api` depends only on `core`.
- **The client stays dumb.** Every client capability is an API endpoint — no
  client (web, desktop, our own SSR Lambdas) ever talks to AWS data stores
  directly. Trust boundary:
  [ADR 0001](../../docs/adr/0001-client-stays-dumb-trust-boundary.md).
- **Routes are public unless they carry `@require_auth`** (`api/auth.py`,
  issue #79) — applied *below* the route decorator, or the check never runs.
  Auth verifies the Cognito **access** token (`client_id`, never `aud`) and
  **fails closed**: missing `AUTH_ISSUER_URL`/`AUTH_CLIENT_ID` is a 401 on
  every protected route, never a bypass. `/health`, `POST /v1/waitlist`, and
  `POST /v1/unsubscribe` are deliberately public.
- **Logs are one JSON line per request, metadata only** — never bodies or PII
  (GLBA). A failed auth logs a category (`AuthFailureReason`), never the token
  or a claim.
- **CORS is an exact-origin allowlist** (`core/config.py`), no wildcard.
- **Local dev runs against this machine's real AWS dev table** — there is no
  local DynamoDB emulator; `infra/envs/dev` is the dev database.
