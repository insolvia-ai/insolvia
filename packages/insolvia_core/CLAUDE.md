# packages/insolvia_core — agent rules

The shared Python domain (firms, token verification, the case domain —
cases/debtors/documents/case collections/provenance — and their adapters),
consumed by `services/api`, the admin service, and the MCP service as a
local-path install. Human docs: [`README.md`](README.md).

- **Admission rule: code moves here only when a second service actually
  imports it — never speculatively.** A generically-named shared package is one
  lazy refactor away from becoming the junk drawer every `common/` directory
  turns into; the test for admission is a concrete second importer, not "might
  be useful". One owner per fact still applies — the owner just lives here.
- **The item shapes are the point.** `firms.firm_item` / `firm_user_item` —
  and the case-domain item shapes (`cases.case_item`, the per-entity parse
  functions, `provenance.py`'s invariants) that moved here under ADR 0016's
  admission — exist in one place so the DynamoDB and in-memory stores cannot
  drift, and so two services cannot disagree about what a stored record looks
  like. Never copy a shape into a service; import it.
- **The court registry lives here, not beside the API's other series.**
  `courts.py` loads `regulatory/courts/us-bankruptcy/` (ADR 0014's release
  layout; the record shape is ADR 0024's) because the CASE DOMAIN validates
  `case.court`/`case.division` against it, and the case domain has two
  writers — the API's routes and the admin service's seeder. A registry the
  seeder could not reach would leave the fixtures unvalidated. The data ships
  in the wheel via `pyproject.toml`'s `package-data`; a file that list misses
  loads in this package's tests and raises in every service. Append-only:
  a correction is a new release directory, never an edit.
- **Layering, enforced by `tests/unit/test_architecture.py`:** the domain modules in
  the package root import nothing but each other and the stdlib (PyJWT is the
  one deliberate exception — the signature check *is* the domain rule, see
  `auth.py`); `adapters/` owns boto3 and psycopg; nothing here ever imports a web framework
  or reaches back into a service (`insolvia_api`, `insolvia_admin`).
- **No pins here.** `pyproject.toml` declares loose floors; each consumer's
  `requirements.txt` pins the exact versions its image resolves. Tool versions
  for this package's own CI come from `services/api/requirements-dev.txt` — one
  pin set, not two.
- **Consumers install non-editable.** After editing this package, re-run the
  consumer's `pip install -r requirements.txt` or its tests will run against
  the stale installed copy.
- **A change here rebuilds every consumer.** `api-pr.yml`'s changed-paths and
  `release.yml`'s `api` regex both name this directory; when a new service
  consumes the package, add this path to its PR filter and release regex in the
  same PR that adds the dependency.
- Tests: pytest, in `tests/unit/` (the only tier here — `tests/__init__.py`
  says why), same conventions as the services (`insolvia-testing` skill). Strict mypy is on for `src`.
- **Postgres (ADR 0030) lives in `adapters/postgres/`:** `connect`, the
  forward-only runner (`migrations.py`, files `migrations/NNNN_name.sql`,
  never edited once merged), `queries.py` (`require_rows` for a refused
  conditional write, `keyset_after`/`next_key` for cursors), and `testing.py`
  — the pytest plugin every consumer's `tests/conftest.py` loads, giving a
  test `postgres` (a connection to an empty, migrated database). Store tests
  need the Docker Postgres up: `docker compose -f services/api/docker-compose.yml up -d --wait postgres`.
  The firm stores (`firm_store.py`, `client_binding_store.py`; migration
  `0001`) are here since #621: each row's `item` jsonb is the domain's
  `*_item` shape minus the DynamoDB keys. A store test runs the memory twin
  and the Postgres adapter through one parametrized fixture.
- **Grants are part of every migration (since 0003, #623).** Each service
  has its own role (`api`, `worker`, `admin`, `mcp`, `filing`; `migrator`
  owns). Sealed values (tax ids, the credential vault) are `bytea`, byte for
  byte, via `sealed.py`. The narrow tables: the access log and admin audit
  log are INSERT-only; the vault is INSERT/DELETE plus non-secret columns for
  `api`, and full SELECT for `filing` alone. **A new table's migration must
  GRANT it** — `tests/unit/test_postgres_grants.py` connects as each role and
  fails on any table no role can reach.
