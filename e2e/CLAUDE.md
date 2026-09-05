# `e2e/` — agent rules

Post-deploy tests against **real environments**. Human docs:
[`README.md`](README.md). Setup runbook:
[`../docs/runbooks/staging-e2e-setup.md`](../docs/runbooks/staging-e2e-setup.md).
The tiers and which environment each runs against:
[ADR 0021](../docs/adr/0021-test-tiers-and-seed-fixtures.md). The design
constraints these obey come from issue #40; #80 is the first test.

- **Two projects, by what they may hold.** `tests/flows/` signs in as a seeded
  person and is offered against dev and staging. `tests/smoke/` holds **no
  credentials** — the deployed shell and its build stamp, the sign-in hand-off,
  the API's environment and refusals, the marketing site — and is the **only
  thing that runs against production**, as a detector after the deploy
  (`app-prod.yml`), having first run against staging in the same release. The
  config offers no `flows` project when `E2E_TARGET=production`, and a
  production run passes no password because there is none. Do not add a
  credential to a smoke spec; move it to `flows`.
- **Three targets, one variable.** `E2E_TARGET` (`dev` | `staging` |
  `production`) picks the fixture (`seeds/<target>.json` — production has
  none, by design) and the footer label the smoke project expects. The URLs
  still arrive explicitly — `E2E_BASE_URL`, `E2E_API_URL`, the optional
  `E2E_MARKETING_URL` — with **no defaults**: the old staging default made a
  bare `npm test` aim at deployed staging and needed staging's test password in
  a developer's shell, the one flow that put that secret on a laptop.
- **Staging in CI, dev on a laptop, production only from `app-prod.yml` —
  never staging or production from a laptop.** `scripts/dev-test.sh` aims both
  projects at `http://localhost:3000` / `:8080` and this machine's dev pool,
  and refuses to start if the stack is down or the credentials are unset.
  `infra/envs/dev` registers `http://localhost:3000` as an exact-match Cognito
  origin, which is what makes a real sign-in round trip possible locally.
  **The staging run is still the authoritative one** — only it exercises
  CloudFront, the deployed bundle and the real API Lambda; a local pass is a
  faster loop, not a substitute.
- **Not an npm workspace member, and it must stay that way.** The root
  `package.json`'s `workspaces` array is explicit and this directory is
  deliberately absent from it, for the same reason `apps/insolvia_marketing` is:
  Node resolution walks *up*, so a dependency this package forgot to declare
  would resolve from the root `node_modules` and pass. Its own
  `package-lock.json` and its own `npm ci` are the thing that catches that. The
  reasoning is owned by the `//` comments in the root `package.json` — read them
  before "tidying" this in.
- **Never a required PR check.** These run post-deploy in `app-staging.yml` —
  the last staging leg of `release.yml` — and `app-prod.yml`, not in any
  `*-pr.yml`. Slow, environment-dependent E2E stays out of the required set
  (`docs/reference/architecture.md` § Required status checks); adding it there
  would put staging's availability on the merge path for every PR. Changing
  the required set at all goes through `scripts/update-ruleset.sh` and the
  `insolvia-branch-protection` skill — it is never a side effect of a change
  here.
- **Its failure must keep failing the staging run.** That is the entire point:
  `release.yml` stamps the `insolvia/staging-release` status only after every
  staging job, this one included, succeeds. A `continue-on-error` on the E2E
  job would silently un-gate production. The production smoke job is the
  opposite shape — a detector, after the fact — and must stay honest about it.
- **The repo is public. No credential, pool id or client id in any file here** —
  not as a default, not in a fixture, not in a comment, not in a test title. The
  password arrives as `E2E_TEST_USER_PASSWORD` and is read in exactly one
  module, `support/env.ts`. The client id arrives as `E2E_COGNITO_CLIENT_ID`
  from a Terraform output and is only ever compared, never written down.
- **Addresses are the one carve-out, and only `.test` ones.** The seeded users
  live in `seeds/<target>.json` and the suite reads their addresses from there,
  by `handle`. That is safe *because* every one ends in `.test` — a reserved TLD
  (RFC 2606) that can never be a real mailbox, which is what the rule above was
  protecting against — and because nothing is mailed to them. **A real
  mailbox must never appear in that fixture**, and if one ever does, the rule
  above applies to it in full. Keeping the addresses there is what stops "who
  the environment seeded" and "who a spec signs in as" becoming two answers.
- **Adding a test user is an edit to `seeds/staging.json`** (and `seeds/dev.json`
  for a local run). Not a script, not a secret, not a new slot in
  `support/env.ts`. The tenancy model is about several people with different
  reach, so a suite that can only sign in as one of them cannot test what the
  model exists for. Adding a case a spec can rely on is an edit to the same
  files plus `seeds/fixtures/<version>/`.
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
  contract. The smoke project's one text selector — the footer's
  `Insolvia · <Label> · <host> · <stamp>` line — is a contract with
  `app-shell.tsx` for the same reason.
