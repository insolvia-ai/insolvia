# services/mcp — agent rules

The official MCP Python SDK + Mangum on Lambda (ADR 0016). Human docs:
[`README.md`](README.md). Run with `scripts/dev-up.sh`; test gate
`scripts/dev-test.sh` (ruff + mypy + pytest, exactly as CI).

- **This is docs/reference/mcp-surface.md, implemented.** Eight tools, the
  candidate-write flow, the pagination contract, and the error vocabulary are
  all decided THERE — change the design doc first, then this service, never
  the reverse. The protocol facts it cites are spec revision 2026-07-28.
- **Layered `core / api / adapters / entrypoints`**, enforced by
  `tests/test_architecture.py`: `core` is the surface's meaning (tool logic,
  config) and never imports the MCP SDK; `api` owns the SDK the
  way the tenant API's api layer owns Flask; adapters own boto3. The case
  domain itself lives in [`packages/insolvia_core`](../../packages/insolvia_core/CLAUDE.md)
  — this service composes its stores and parse functions, and MUST NOT copy a
  shape or re-derive an access rule locally.
- **There is no code path that writes a case record.** Agent writes land as
  candidates (`insolvia_core.candidates`, `CANDIDATE#` rows) and become case
  data only in the review flow (8.9) — confirm-before-entry holds
  structurally, not as a check. Do not add a case-record write "just for
  tests"; the absence is the invariant.
- **The candidate module graduated.** It began here under the core package's
  admission rule (one composer) and moved to `insolvia_core` verbatim when
  the extraction/review flow (8.7-8.9) became its second importer — the same
  path the case domain took. Change candidate shapes there, never locally.
- **Every call resolves the accessor from the store — never cached** (two
  reads, ADR 0009). The firm is never an argument; the origin of a proposal
  comes from the VERIFIED token. Tools the caller may not use are listed but
  refuse (`permission_denied`); another firm's caseId answers `not_found`.
- **Token verification is `insolvia_core.auth`, unchanged**, composed into
  the SDK's `TokenVerifier` seam (`api/auth.py`) and failing closed on every
  path including missing config. This service verifies its OWN client id(s),
  disjoint from the app's — that exactness IS the audience check, because
  Cognito access tokens carry no `aud`.
- **Stateless on purpose.** `streamable_http_app(stateless_http=True,
  json_response=True)`: revision 2026-07-28 removed protocol sessions, our
  tools never need a mid-call SSE stream, and Lambda wants exactly this
  shape. Anything long-running does not belong here (ADR 0018 — a pipeline
  job, via the API).
- **Logs are one JSON line, metadata only** — tool name, ids, coarse auth
  categories; never arguments, payloads, tokens, or claims (GLBA). Case and
  document reads through this surface land in the SAME access log the API
  writes.
- **Local dev runs against this machine's real AWS dev tables** — no
  emulator; `infra/envs/dev` is the dev database, `scripts/dev-aws-setup.sh`
  writes `services/mcp/.env`. The bare server (no .env) is in-memory with
  auth failing closed.
- **The MCP protocol seam is pinned** in `tests/test_protocol.py` the way
  the api-client contract test pins the REST surface: real JSON-RPC bodies,
  real headers, real RS256 tokens. A wire-visible change is SUPPOSED to break
  it.
