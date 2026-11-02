# `e2e/` — end-to-end and smoke tests against deployed environments

Playwright (Chromium) tests that drive **real deployed environments** over the
public internet, in two projects:

- **`flows`** — signs in through the Cognito hosted UI as a seeded person and
  drives the app. Runs against **staging** in `.github/workflows/app-staging.yml`
  after the S3 sync and the CloudFront invalidation, and against a developer's
  own dev stack. Its failure fails the staging run, which is what makes it gate
  the `insolvia/staging-release` status and therefore production promotion.
- **`smoke`** — holds **no credentials**. Proves the deployed shell is this
  environment's and this commit's (the footer's label and build stamp), that a
  deep link survives the SPA rewrite, that "Sign in" hands off to this
  environment's own pool with the registered client and callback, that the API
  names its environment, refuses an anonymous call with 401 and admits only the
  app's origin, and that the marketing site serves. Runs against staging in the
  same job, and against **production** in `.github/workflows/app-prod.yml` as
  the release's last leg — a detector, after the deploy, never a gate.

Agent rules: [`CLAUDE.md`](CLAUDE.md). One-time setup:
[`../docs/runbooks/staging-e2e-setup.md`](../docs/runbooks/staging-e2e-setup.md).
The tiers: [ADR 0021](../docs/adr/0021-test-tiers-and-seed-fixtures.md).

| File | What |
|---|---|
| `playwright.config.ts` | Runner config — the two projects, retries, timeouts, and the artifact policy that keeps a password out of a public repo's build artifacts. Offers no `flows` project when the target is production. |
| `support/env.ts` | The one place the target and credentials enter the suite. `E2E_TARGET` picks the fixture and the expected labels; no URL has a default; the password has none and never will. |
| `support/sign-in.ts` | The sign-in dance, for `flows` specs whose subject is something else. `auth-round-trip.spec.ts` deliberately does not use it — the steps it skips past are that spec's whole point. |
| `support/scratch-residence.ts` | Gives the scratch client a home (county included) on its record and re-copies it onto the scratch case's Debtor 1 — the path a real address takes, and the one the means test reads. |
| `support/capability.ts` | Follows a presigned link the app opens in a new tab and returns its status and bytes, never the URL. |
| `support/scratch-case.ts` | The suite's one scratch case, for one scratch client, found or opened through the UI — the only case a `flows` spec may write to. Seeded fixture cases are read-only here. |
| `tests/flows/auth-round-trip.spec.ts` | Sign in via the Cognito hosted UI → `/auth/callback` → the signed-in identity renders → sign out. |
| `tests/flows/intake-persists.spec.ts` | Type into the scratch case's intake, watch it save, reload, find it still there. The only test that proves the app's request body is one the API accepts and the store keeps. |
| `tests/flows/full-case.spec.ts` | One case front to back: the debtor's address via the client record → the overview counts it → documents → the forms hub lists the Chapter 7 set and renders a form to PDF → the filing set orders it for the court and holds it on the petition, linking to the fix. Never presses "Assemble" (a queued job). |
| `tests/flows/documents.spec.ts` | A document request asked for, set aside and withdrawn; a file uploaded from the browser to the bucket, downloaded byte-identical and deleted. Each test sweeps an earlier failed run's leftovers first, so nothing accumulates. |
| `tests/flows/means-test.spec.ts` | One income moved across the median: the verdict follows (no presumption → presumption arises) and survives a reload. |
| `playwright.admin.config.ts` | A separate config for the admin portal's flow — no target, no password, nothing deployed. `npx playwright test` never reads it. |
| `support/google-identity-stand-in.ts` | Answers Google's sign-in script inside the browser with a button that returns an ID token signed by the offline stack's per-run key. Only that throwaway stack trusts the key; no host or deployed config knows the stand-in exists. |
| `tests/admin/provision-and-invite.spec.ts` | Staff sign in to the admin portal, provision a firm, find its administrator under People, re-send the invitation. Offline only. |
| `tests/smoke/app.spec.ts` | The shell, the footer's environment label and build stamp, the SPA rewrite, the sign-in hand-off (client id, callback, PKCE, the managed-login form) — without a password. |
| `tests/smoke/api.spec.ts` | `/health` names the environment; an anonymous protected call is a 401, not a 500; CORS admits the app's origin and refuses a stranger's. |
| `tests/smoke/marketing.spec.ts` | The home page serves; the indexing policy is the right one for a non-production host. Skipped by name when no marketing URL is given. |

## Running it

**Against staging and production: CI only.** `app-staging.yml` runs both
projects after every staging deploy and `app-prod.yml` runs `smoke` after
every production deploy, passing the URLs, the pool's hosted domain and client
id, and the commit's sha from Terraform outputs and the run itself. There is no
local-against-staging flow — it would put staging's secret in a developer's
shell for a run CI already does — and no URL has a default, so a bare
`npx playwright test` refuses rather than quietly aiming at a deployed
environment.

**Locally: against this machine's dev stack**, via the wrapper (it is **not**
an npm workspace member — see the `//` note in `package.json` — so it installs
on its own):

```bash
cd e2e && npm ci && npm run browser   # once; browser = playwright chromium
```

```bash
./e2e/scripts/dev-test.sh                  # both projects, headless
./e2e/scripts/dev-test.sh --headed         # watch it drive the browser
./e2e/scripts/dev-test.sh --project smoke  # only the unauthenticated half
```

The wrapper needs `scripts/dev-up.sh` running (the app on :3000, the API on
:8080) and `scripts/dev-aws-seed.sh` done. The password comes from
`~/.config/insolvia/dev.env` (which the seed script offers to write) or an
exported `E2E_TEST_USER_PASSWORD`; the address comes from `seeds/dev.json`.
Credentials have **no default** and the run fails at config load, naming the
variable, if none is available. The marketing checks skip locally — a dev
stack has no marketing site of its own.

The admin portal's flow needs neither the dev stack nor a password. It runs
over the contract suite's throwaway stack, and the PR workflow
`admin-flow-pr.yml` runs the same command. That check is not required.

```bash
./e2e/scripts/admin-offline-test.sh        # Docker + the .NET SDK; npm ci in e2e/ and apps/insolvia_admin/
```

## Two things to know before changing a test

- **The selectors are a contract with the app**, by accessible name and role
  (`Sign in`, `Sign out`, the email as visible text, the footer line). Renaming
  a button in `apps/insolvia_app` breaks this suite, and the staging deploy is
  where you find out. The spec's header comment carries the full list.
- **No `waitForTimeout`.** Every wait is on a condition. A sleep added here is
  a flake added to the production-promotion path.
