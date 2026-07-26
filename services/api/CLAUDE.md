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
- **Logs are one JSON line per request, metadata only** — never bodies or PII
  (GLBA).
- **CORS is an exact-origin allowlist** (`core/config.py`), no wildcard.
- **Local dev runs against this machine's real AWS dev table** — there is no
  local DynamoDB emulator; `infra/envs/dev` is the dev database.
