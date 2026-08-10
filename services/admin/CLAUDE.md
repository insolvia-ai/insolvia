# services/admin — agent rules

Flask + Mangum on Lambda: cross-tenant firm administration (#212) — the
provisioning surface #178 asked for. Human docs: [`README.md`](README.md).
Gate: `scripts/dev-test.sh` (ruff + mypy + pytest, exactly as CI).

- **Layered `core / api / adapters / entrypoints`**, enforced by
  `tests/test_architecture.py` — same direction rules as the tenant API,
  plus one of its own: **`insolvia_api` is out of bounds everywhere**;
  shared domain comes only from
  [`packages/insolvia_core`](../../packages/insolvia_core/CLAUDE.md)
  (non-editable install — re-run `pip install -r requirements.txt` after
  editing the package).
- **The caller is a STAFF principal, never a firm user** — a Google
  Workspace ID token (`@require_staff`, issuer + audience + `hd` checks in
  `insolvia_core.auth`), and the cross-issuer 401 is the service's security
  invariant: a firm-pool token must die in verification, and
  `tests/test_firm_routes.py` pins it. There is deliberately no third
  authenticated-but-unpermitted state here — the Workspace check IS the
  authorization ([ADR 0011](../../docs/adr/0011-cross-tenant-administration-is-a-separate-principal-class.md)).
- **Firm ids appear in URLs here**, which ADR 0009 forbids the tenant API —
  the routes module's docstring owns why the rule does not transfer. Every
  read that takes one still resolves firm-scoped.
- **Every mutation writes an audit row** (`core/audit.py`) naming the staff
  caller — #178's hard requirement. The table grant is PutItem only; the
  provenance a portal DISPLAYS comes from the firm item's `createdBy`
  fields, never from reading this log.
- **The seeder lives here** (`entrypoints/seed.py`) — provisioning tooling
  has one home. Its dev/staging-only fence is load-bearing; prod provisioning
  is this service's HTTP surface, with the audit trail, and nothing else.
- Local dev: the suite and bare dev server run fully in-memory; the compose
  stack (`scripts/dev-up.sh`, port **8090** — the API owns 8080) talks to
  this machine's real dev resources once `services/admin/.env` exists
  (written by `scripts/dev-aws-setup.sh` when the admin dev infra lands,
  #213). `GOOGLE_CLIENT_ID` unset fails CLOSED — staff routes 401.
- Logs are one JSON line per request, metadata only — never bodies, never
  claims, and never a firm name next to an error.
