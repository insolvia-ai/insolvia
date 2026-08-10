# packages/insolvia_core — agent rules

The shared Python domain (firms, token verification, their adapters), consumed
by `services/api` and the admin service as a local-path install. Human docs:
[`README.md`](README.md).

- **Admission rule: code moves here only when a second service actually
  imports it — never speculatively.** A generically-named shared package is one
  lazy refactor away from becoming the junk drawer every `common/` directory
  turns into; the test for admission is a concrete second importer, not "might
  be useful". One owner per fact still applies — the owner just lives here.
- **The item shapes are the point.** `firms.firm_item` / `firm_user_item` exist
  in one place so the DynamoDB and in-memory stores cannot drift, and so two
  services cannot disagree about what a stored firm looks like. Never copy a
  shape into a service; import it.
- **Layering, enforced by `tests/test_architecture.py`:** the domain modules in
  the package root import nothing but each other and the stdlib (PyJWT is the
  one deliberate exception — the signature check *is* the domain rule, see
  `auth.py`); `adapters/` owns boto3; nothing here ever imports a web framework
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
- Tests: pytest, colocated in `tests/`, same conventions as the services
  (`insolvia-testing` skill). Strict mypy is on for `src`.
