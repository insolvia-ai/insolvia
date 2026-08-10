# ADR 0012 — The firm domain moves to a shared Python package

- **Status:** Accepted
- **Date:** 2026-08-10
- **Relates to:** enables the admin service of
  [ADR 0011](0011-cross-tenant-administration-is-a-separate-principal-class.md)
  (forthcoming — issue [#212](https://github.com/insolvia-ai/insolvia/issues/212));
  leaves [ADR 0009](0009-a-case-belongs-to-a-firm.md)'s model untouched and
  moves its implementation. Issue
  [#208](https://github.com/insolvia-ai/insolvia/issues/208).

## Decision

**The firm domain, Cognito access-token verification, and their adapters leave
`insolvia_api` for a shared package, `packages/insolvia_core`, installed by
each Python service from a local path and baked into its image.**

What moved, verbatim and without behavior change:

| Into | From `services/api/src/insolvia_api/` |
|---|---|
| `insolvia_core.firms` | `core/firms.py` — entities, parsing, permissions, `firm_item`/`firm_user_item` |
| `insolvia_core.auth` | `core/auth.py` — RS256/JWKS verification, issuer passed in |
| `insolvia_core.errors` | `core/errors.py` — the error vocabulary the routes map to statuses |
| `insolvia_core.ports` | `core/ports.py`'s `FirmStore`, `UserDirectory`, `JwksProvider` |
| `insolvia_core.adapters.aws` | the firm store, user directory, and JWKS adapters |
| `insolvia_core.adapters.memory` | their in-memory counterparts |

The proof of "without behavior change" is mechanical: the API's own test suite
passes unedited except for import paths.

## Context

The admin service ([#212](https://github.com/insolvia-ai/insolvia/issues/212))
provisions firms — which means it writes the same DynamoDB items, runs the same
parsers, and verifies tokens through the same checks as the tenant API. Three
ways to get there:

1. **Copy the code.** Rejected outright by the codebase's own argument:
   `firm_item`'s docstring puts the item shapes in one place *so the stores
   cannot drift*. Two services independently writing firm items is that drift,
   except the divergence corrupts tenant data instead of failing a test — a
   `GSI1PK` omitted by one copy is a user who silently cannot sign in.
2. **The admin service pip-installs `insolvia_api` itself.** Zero refactor, but
   the admin image would carry Flask, the routes, and every case/document
   module as an implementation detail, and "what may the admin service reach"
   would be answered by discipline rather than by what its dependency exports.
3. **Extract the shared subset into its own package.** A real refactor of the
   most heavily tested module in the repo — but the boundary then *is* the
   dependency: the admin service imports a package that contains firms and
   auth, and structurally cannot import a case store that is not there.

Option 3, with the package named for what it is (`insolvia_core`, matching the
`core` vocabulary both services already use internally) rather than for its
first tenant — the token-verification module proved the point immediately by
being needed on day one and having nothing to do with firms.

## Consequences

- **One npm workspace, one Python package — and they do not meet.** The
  package lives under `packages/` beside `insolvia_api_client` but is *not* a
  workspace member (`tool/` set the precedent for root-adjacent non-members).
  Consumers name it in `requirements.txt` as `../../packages/insolvia_core`,
  which pip resolves relative to the requirements file — the same line works
  from a checkout and inside a Docker build.
- **Service image builds now take the repo root as context.** A
  `services/api`-scoped context cannot see a sibling package. `api-pr.yml`,
  `api-staging.yml`, `scripts/bootstrap-ecr-images.sh`, and the compose file
  all pass the root and name the Dockerfile explicitly; prod is untouched
  because it promotes a digest rather than rebuilding.
- **A core change is a consumer change.** `api-pr.yml`'s changed-paths filter
  and `release.yml`'s `api` regex both name `packages/insolvia_core/` — the
  package ships *inside* the image, so editing it must re-test and redeploy
  every service that installs it. A future consumer adds itself to both in the
  PR that adds the dependency. `release.yml`'s `app` regex was tightened from
  `^apps/|^packages/` to the paths the app actually consumes at the same time,
  so core changes stop triggering app deploys.
- **No second pin file.** The package declares loose floors; each consumer's
  `requirements.txt` keeps the exact pins its image resolves. The package's own
  CI (`Core package`, `core-pr.yml`) installs the API's requirements files for
  the same reason — one set of tool pins.
- **The admission rule is written at the door.** The package's `CLAUDE.md`:
  code moves in only when a second service actually imports it, never
  speculatively. The rule is what keeps a generically-named package from
  becoming the junk drawer.
- **Non-editable installs have a sharp edge:** after editing the package, a
  consumer's tests run against the stale installed copy until its
  `pip install -r requirements.txt` re-runs. Documented in the package README;
  the CI ordering (fresh install every run) makes it a local-only hazard.

## Revisit when

- A third Python service consumes the package with a materially different
  subset — the signal to split `insolvia_core` by domain rather than grow it.
- Anything in it needs to be consumed *outside* this repository — that is a
  publishing decision this ADR deliberately does not make.
