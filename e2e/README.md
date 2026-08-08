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

It is **not** an npm workspace member (see the `//` note in `package.json`), so
it installs on its own:

```bash
cd e2e
npm ci
npm run browser                     # playwright install --with-deps chromium

read -rs E2E_TEST_USER_PASSWORD && export E2E_TEST_USER_PASSWORD
npm test
```

Optional overrides, both public values with sane defaults:

| Variable | Default | What |
|---|---|---|
| `E2E_BASE_URL` | `https://staging-app.insolvia.ai` | Origin under test. CI passes the Terraform `url` output. |
| `E2E_COGNITO_DOMAIN` | — | Exact hosted-UI hostname to assert. CI passes the Terraform `auth_domain` output; unset falls back to asserting any `*.amazoncognito.com` host. |

`E2E_TEST_USER_PASSWORD` has **no default** and the
run fails at config load if either is missing.

## Two things to know before changing a test

- **The selectors are a contract with the app**, by accessible name and role
  (`Sign in`, `Sign out`, the email as visible text). Renaming a button in
  `apps/insolvia_app` breaks this suite, and the staging deploy is where you
  find out. The spec's header comment carries the full list.
- **No `waitForTimeout`.** Every wait is on a condition. A sleep added here is
  a flake added to the production-promotion path.
