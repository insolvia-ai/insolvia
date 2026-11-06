# ADR 0030 — Local development signs in through a local server, not the dev Cognito pool

- **Status:** Accepted 2026-11-05.
- **Date:** 2026-11-05.
- **Relates to:** [ADR 0026](0026-auth-is-a-shared-service.md) keeps the
  deployed pools in insolvia-services; this changes only what a laptop signs in
  against. [ADR 0007](0007-hosted-ui-pkce-refresh-token-in-local-storage.md) is
  the flow the local server reproduces.
  [ADR 0011](0011-cross-tenant-administration-is-a-separate-principal-class.md)
  is why the admin portal is untouched: staff sign in through Google.
  [`docs/runbooks/aws-paused.md`](../runbooks/aws-paused.md) records the
  teardown that prompted this.

## Decision

**Local development signs in through `backend/local-auth/server.ts`, a small
server in the compose stack that stands in for the Cognito hosted domain.** It
runs on `http://localhost:8071`, and `scripts/dev-local-setup.sh` points
`backend/.env` and the app's `.env` at it. Daily development needs no AWS
account.

- **It speaks what the pool speaks.** Authorization code with PKCE S256,
  RS256 tokens shaped like Cognito's (`token_use`, `client_id`, no `aud` on an
  access token), a JWKS, discovery, refresh rotation with a 30-second grace,
  and exact-match callback and logout URLs. The app, the API and the MCP server
  run the code they run deployed; only the issuer differs.
- **It has no passwords.** The page lists the seeded people and takes any
  address. It listens on `127.0.0.1` only. The signing key is generated at
  start, and codes and refresh tokens live in memory, so a restart signs
  everyone out.
- **Subjects are derived from the address.** `LocalSubjects.For` and
  `subjectFor` compute the same UUIDv5, pinned on both sides by one vector, so
  the migrator's seed and a token agree with no shared store.
  `INSOLVIA_LOCAL_SUBJECTS=derived` gives the API and admin hosts' in-memory
  directory the same subjects.
- **A database seeded from the pool keeps working.** `dev-local-setup.sh`
  reads each address and subject from the local database into
  `LOCAL_AUTH_SUBJECTS`, and the server signs those people in under their old
  subject. No local data has to be wiped.
- **Every switch is local only.** `AUTH_JWKS_URL` and
  `INSOLVIA_LOCAL_SUBJECTS` are honoured only when `INSOLVIA_ENV=local`, and
  `AUTH_JWKS_URL` never in Lambda. The app accepts an `http://localhost` auth
  domain only when its environment is `local`.

## Context

The dev, staging and production environments were torn down on 2026-11-05, and
development is local until there is a full MVP. Sign-in was the last daily
dependency on AWS: every developer's app signed in against the shared
`insolvia-dev-users` pool, and its ids came from SSM. That meant a live AWS
session to set up a machine and a pool kept alive for no other reason.

The alternatives were keeping the pool, which costs nothing but keeps the AWS
login in the daily path, and an emulator or an off-the-shelf identity server.
Either of those adds a third-party service to run, and a general identity
server issues its own token shapes rather than the `token_use` and `client_id`
claims token verification requires. The server here is one file with no
dependencies, and its tests pin those claims.

## Consequences

- `scripts/dev-local-setup.sh` replaces `dev-aws-setup.sh` and `dev-aws-seed.sh`
  for local work. The `dev-aws-*` scripts stay for when AWS comes back.
- The issuer is the address a browser uses, because the MCP server advertises
  it. Containers reach the same keys through `AUTH_JWKS_URL`, over the compose
  network.
- Local sign-in proves nothing about the real pool: hosted-UI branding, invite
  email, password policy and MFA go untested until a deployed environment
  returns. `contract/`'s deployed target and the staging E2E run remain the
  checks for that.
- `contract/scripts/dev-test.sh --target deployed` cannot run against a laptop
  any more: it signs in by SRP, which `local-auth` does not speak. The default
  target is unaffected.
- Anyone who can reach port 8071 on the laptop can sign in as anyone. That is
  why it binds to `127.0.0.1`, and why it must never run anywhere else.
- A new app client or callback URL now has two homes: insolvia-services for
  the deployed pools, and `CLIENTS` in `server.ts` for local.
