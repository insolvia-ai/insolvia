# services/api — agent rules

Flask + Mangum on Lambda. Human docs: [`README.md`](README.md). Run with
`scripts/dev-up.sh`; test gate `scripts/dev-test.sh` (ruff + pytest, exactly as CI).

- **Layered `core / api / adapters / entrypoints`** with the dependency
  direction enforced by `tests/test_architecture.py`: `core` depends on nothing
  else; `api` depends only on `core`. The firm domain, token verification, and
  their adapters live in the shared
  [`packages/insolvia_core`](../../packages/insolvia_core/CLAUDE.md)
  ([ADR 0012](../../docs/adr/0012-shared-python-domain-package.md)) — its
  domain modules count as core-direction imports, its `adapters` as adapters.
  It is installed from `requirements.txt` by local path and is NOT editable:
  after editing the package, re-run `pip install -r requirements.txt` here.
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
- **There is a third state: authenticated *and permitted*.** A case belongs to
  a **firm**, not to whoever opened it. `current_accessor()` resolves the
  caller's firm user (`core/firms.py`) and answers **403** when there is none —
  a different failure from 401, and not hidden behind a 404 because it is a
  fact about the caller's own account. `@requires(FEATURE, LEVEL)` goes below
  `@require_auth`, same "or it never runs" rule. Everything about who may see
  which case is `core/access.may_see_case`, in one place on purpose.
  `/v1/me` is the ONE route that resolves without requiring — it reports the
  firm, or reports its absence, so a new user has something to render.
- **Logs are one JSON line per request, metadata only** — never bodies or PII
  (GLBA). A failed auth logs a category (`AuthFailureReason`), never the token
  or a claim.
- **CORS is an exact-origin allowlist** (`core/config.py`), no wildcard.
- **Local dev runs against this machine's real AWS dev table** — there is no
  local DynamoDB emulator; `infra/envs/dev` is the dev database.
- **Minutes-long work is a pipeline job, never an endpoint**
  ([ADR 0018](../../docs/adr/0018-sqs-queue-and-worker-lambda-over-step-functions.md)):
  the API accepts a job (`api/routes/jobs.py`) and a separate worker Lambda
  runs it. A new job kind is a plain callable: dependency-free workers
  register in `core/jobs.WORKERS`; store-reading ones (packet assembly,
  `core/packet_assembly.py`) are composed by the worker entrypoints, with
  their kind named in `core/jobs.KINDS` so the accept endpoint admits it —
  heavy dependencies go in the **worker** Docker stage, not the API's.
  `core/jobs.py` owns the queue message contract; both entrypoints
  (`worker_lambda`, the local `worker_poller`) parse with it, and
  `tests/test_jobs.py` pins the wire shape.
