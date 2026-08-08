# `e2e/` — agent rules

Post-deploy end-to-end tests against **real staging**. Human docs:
[`README.md`](README.md). Setup runbook:
[`../docs/runbooks/staging-e2e-setup.md`](../docs/runbooks/staging-e2e-setup.md).
The design constraints these obey come from issue #40; #80 is the first test.

- **Two targets: staging in CI, local dev when you run it yourself.**
  `scripts/dev-test.sh` points `E2E_BASE_URL` at `http://localhost:3000` and
  `E2E_COGNITO_DOMAIN` at this machine's dev pool, and refuses to start if the
  stack is down or the credentials are unset. `infra/envs/dev` already registers
  `http://localhost:3000` as an exact-match Cognito origin, which is what makes
  a real sign-in round trip possible locally. **The staging run is still the
  authoritative one** — only it exercises CloudFront, the deployed bundle and
  the real API Lambda; a local pass is a faster loop, not a substitute.
- **Not an npm workspace member, and it must stay that way.** The root
  `package.json`'s `workspaces` array is explicit and this directory is
  deliberately absent from it, for the same reason `apps/insolvia_marketing` is:
  Node resolution walks *up*, so a dependency this package forgot to declare
  would resolve from the root `node_modules` and pass. Its own
  `package-lock.json` and its own `npm ci` are the thing that catches that. The
  reasoning is owned by the `//` comments in the root `package.json` — read them
  before "tidying" this in.
- **Never a required PR check.** These run post-deploy in `app-staging.yml` —
  the last staging leg of `release.yml` — not
  in any `*-pr.yml`. Slow, environment-dependent E2E stays out of the required
  set (`docs/reference/architecture.md` § Required status checks); adding it
  there would put staging's availability on the merge path for every PR.
  Changing the required set at all goes through `scripts/update-ruleset.sh` and
  the `insolvia-branch-protection` skill — it is never a side effect of a change
  here.
- **Its failure must keep failing the staging run.** That is the entire point:
  `.github/actions/verified-commit` blesses a commit for production only if the
  staging run concluded successfully. A `continue-on-error` on the E2E job would
  silently un-gate production.
- **The repo is public. No credential, pool id or client id in any file here** —
  not as a default, not in a fixture, not in a comment, not in a test title. The
  password arrives as `E2E_TEST_USER_PASSWORD` and is read in exactly one
  module, `support/env.ts`.
- **Addresses are the one carve-out, and only `.test` ones.** The seeded users
  live in `seeds/staging.json` and the suite reads their addresses from there,
  by `handle`. That is safe *because* every one ends in `.test` — a reserved TLD
  (RFC 2606) that can never be a real mailbox, which is what the rule above was
  protecting against — and because nothing is mailed to them. **A real
  mailbox must never appear in that fixture**, and if one ever does, the rule
  above applies to it in full. Keeping the addresses there is what stops "who
  the environment seeded" and "who a spec signs in as" becoming two answers.
- **Adding a test user is an edit to `seeds/staging.json`.** Not a script, not a
  secret, not a new slot in `support/env.ts`. The tenancy model is about several
  people with different reach, so a suite that can only sign in as one of them
  cannot test what the model exists for.
- **Do not upload Playwright artifacts from CI, and do not turn traces on
  there.** A trace records `fill()` arguments verbatim — including the password
  — and Actions artifacts on a public repo are downloadable by anyone with the
  run URL. `playwright.config.ts` disables both in CI and explains it; that
  comment is the reasoning, don't discard it while "improving debuggability".
- **No `waitForTimeout`, ever.** Wait on conditions. A flaky test here does not
  just annoy — it blocks production promotion for the commit.
- **Selectors are role-based, by accessible name**, matching the accessibility
  discipline `app-pr.yml`'s axe audit enforces on the app. Changing one is a
  coordinated change with `apps/insolvia_app`; the spec's header comment is the
  contract.
