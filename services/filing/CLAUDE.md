# services/filing — agent rules

The filing worker (ADR 0024 PR 7). Human docs: [`README.md`](README.md). Gate:
`scripts/dev-test.sh` (ruff + mypy + pytest, exactly as CI); the real-AWS
proof: `scripts/dev-filing-proof.sh`.

- **Nothing here may contact a real court, PACER, or any host but the fake.**
  `core/fence.py`'s allowlist is the ONLY way a host becomes reachable, and it
  is a constant — never read a host from configuration, a message, a court
  registry record or a page. A court joins in ADR 0024 PR 10, in a reviewed
  diff, after its driver opened the fixture case on that court's training
  database. Every HTTP request goes through `adapters/http/fenced_client.py`;
  `tests/unit/test_fence.py` fails if any other module imports a socket, an
  HTTP client or a URL opener. A headless browser (PR 10) must route through
  the same fence.
- **Never a second submission.** Every state change is `_Run._move` — a
  conditional write on (state, attempt). `at_final_submit` is written BEFORE
  the click; after it, anything uncertain is `outcome_unknown`, never
  retried, never handed back. Do not add a retry, a "resume from
  at_final_submit", or a catch that turns a post-mark failure into a
  hand-back. The fake counts submissions; every never-twice test asserts it.
- **Stop, don't guess.** A screen the driver does not recognise
  (`drivers/screens.ScreenSpec`), a court message, a timeout — stop and hand
  back. No clicking forward, no altered input. Every reason the code can stop
  for must have an entry in `core/hand_back.py` (`tests/unit/test_hand_back.py`
  reads the source).
- **Before the credential is opened:** the approval consumed, the kill switch
  on, a driver for the court, and every driver origin inside the fence. Before
  the final submit, all of it again plus the digest. Keep that order.
- **Secrets live in one frame.** The password and TOTP seed exist in
  `worker._sign_in` and the driver call; the TOTP is computed at the court's
  ask. Never log, store, raise or return them (`test_worker_guards` searches
  logs, records and access rows).
- **The approval and the filing set are the API's** — `insolvia_api.core`,
  imported, never copied (README, "Why the worker imports insolvia_api.core").
  Only the modules in `tests/unit/test_architecture.py`'s `ALLOWED_API`; a new
  one is a reviewed change to that list.
- **The fake court is a fixture.** `fake/fake_cmecf` stays outside `src/`;
  nothing in `src/` imports it; the Dockerfile never copies it. Its fingerprints
  are the driver's `SCREENS` — change both together.
- **Layered `core / adapters / entrypoints`** (`test_architecture.py`): `core`
  never imports boto3 or an adapter.
- **Tests**: one tier, `tests/unit/`, no AWS; the fake runs in-process on
  127.0.0.1. The suite imports the API's reference case and approval world
  from `services/api/tests/unit` (see `tests/conftest.py` for why it runs with
  `--import-mode=importlib` and has no `tests` package).
