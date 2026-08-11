# `e2e/` — end-to-end tests against deployed staging

Playwright (Chromium) tests that drive **real deployed staging** over the public
internet. They run in `.github/workflows/app-staging.yml` after the S3 sync and
the CloudFront invalidation, and their failure fails the staging run — which is
what makes them gate `.github/actions/verified-commit` and therefore production
promotion. Agent rules: [`CLAUDE.md`](CLAUDE.md). One-time setup:
[`../docs/runbooks/staging-e2e-setup.md`](../docs/runbooks/staging-e2e-setup.md).

| File | What |
|---|---|
| `playwright.config.ts` | Runner config — retries, timeouts, and the artifact policy that keeps a password out of a public repo's build artifacts. |
| `support/env.ts` | The one place credentials enter the suite. No defaults, no fixtures, nothing echoed. |
| `support/sign-in.ts` | The sign-in dance, for specs whose subject is something else. `auth-round-trip.spec.ts` deliberately does not use it — the steps it skips past are that spec's whole point. |
| `tests/auth-round-trip.spec.ts` | Sign in via the Cognito hosted UI → `/auth/callback` → the signed-in identity renders → sign out. |
| `tests/intake-persists.spec.ts` | Type into a case's intake, watch it save, reload, find it still there. The only test that proves the app's request body is one the API accepts and the store keeps. |

## Running it

**Against staging: CI only.** `app-staging.yml` runs the suite after every
staging deploy, passing `E2E_BASE_URL`, `E2E_COGNITO_DOMAIN` and the test-user
password from Terraform outputs and the environment secret. There is no
local-against-staging flow — it would put staging's secret in a developer's
shell for a run CI already does, and `E2E_BASE_URL` deliberately has no
default so a bare `npm test` refuses rather than quietly aiming at staging.

**Locally: against this machine's dev stack**, via the wrapper (it is **not**
an npm workspace member — see the `//` note in `package.json` — so it installs
on its own):

```bash
cd e2e && npm ci && npm run browser   # once; browser = playwright chromium
```

```bash
./e2e/scripts/dev-test.sh             # or --headed to watch it
```

The wrapper needs `scripts/dev-up.sh` running and `scripts/dev-aws-seed.sh`
done. The password comes from `~/.config/insolvia/dev.env` (which the seed
script offers to write) or an exported `E2E_TEST_USER_PASSWORD`; the address
comes from `seeds/dev.json`. Credentials have **no default** and the run fails
at config load, naming the variable, if none is available.

## Two things to know before changing a test

- **The selectors are a contract with the app**, by accessible name and role
  (`Sign in`, `Sign out`, the email as visible text). Renaming a button in
  `apps/insolvia_app` breaks this suite, and the staging deploy is where you
  find out. The spec's header comment carries the full list.
- **No `waitForTimeout`.** Every wait is on a condition. A sleep added here is
  a flake added to the production-promotion path.
