# Staging E2E — one-time setup

**State:** open — actionable now. Run once; after that the E2E job runs itself
on every staging deploy.

The post-deploy suite in [`e2e/`](../../e2e/README.md) signs in against real
staging through the Cognito hosted UI. It needs two things that cannot be
created by merging code: a **user in the staging pool**, and **that user's
credentials in GitHub Actions**. This runbook is the order to create them in and
how to tell it worked.

Why it is worth doing: production only ships behind a green staging stage of
`release.yml` (in-run via `needs`, or via the `insolvia/staging-release`
commit status for a hand-dispatched deploy) — so the strength of the
production gate is exactly the strength of what the staging run asserts. Until this suite existed, the App staging run asserted nothing at
all. See issue #40 for the shape of the suite and #80 for this first test.

## Before you start

- A **staging-capable AWS session**. If it is not working, read the
  `insolvia-aws-auth` skill; the credential mechanics live there.
- A `gh` login with write access to the repository's Actions secrets.
- A **dedicated synthetic email address** for the test user — never a real
  person's mailbox, and never an address you would mind seeing in a Cognito
  console. Nothing sends mail to it (the create script suppresses Cognito's
  invitation email), so it does not have to receive anything.
- A password you generate yourself, meeting the pool policy: **12+ characters
  with a lower-case letter, an upper-case letter and a digit** (symbols allowed,
  not required — `infra/modules/auth/main.tf`).

**The repo is public.** Neither value goes into a file here, a commit message,
or a PR description. Both scripts read them from your environment or prompt for
them without echo.

## The order

Two scripts, catalogued in [`../../scripts/README.md`](../../scripts/README.md)
— run them from the repo root. Their `--help` and their header comments own the
detail; this is only the order and the checkpoints.

```bash
export E2E_TEST_USER_EMAIL='…'          # the synthetic address

# 1. Create the user in the STAGING Cognito pool.
./scripts/staging-aws-create-test-user.sh       # prompts for the password, without echo

# 2. Put the same two values in the insolvia-staging ENVIRONMENT secrets.
./scripts/staging-github-set-secrets.sh            # prompts again; or export E2E_TEST_USER_PASSWORD
```

**Step 1 must come first.** Step 2 only stores strings; it cannot tell you
whether they name a user that exists.

### 3. Two more secrets, and why they are not optional

Signing in is only half of it. A case belongs to a **firm** (ADR 0009), so a
user in none resolves to no accessor and every route behind
`current_accessor()` answers 403 — which shows up as `intake-persists.spec.ts`
failing on a case list that can never populate, blaming the accessible name of
a link that was never rendered.

`app-staging.yml` seeds that firm from
[`../../seeds/staging.json`](../../seeds/staging.json) before the suite runs,
idempotently, on every deploy. It needs two things this runbook has to put in
place once.

**The seed role's ARN.** `infra/envs/ci-trust` grows a second, narrower role
(`insolvia-github-actions-seed`) that may write the staging firm table and
nothing else. **CI cannot apply that root** — see the
`insolvia-deploy-role-permissions` skill — so a human runs:

```bash
./scripts/apply-ci-trust.sh
terraform -chdir=infra/envs/ci-trust output -raw github_seed_role_arn
```

Store the result as `AWS_SEED_ROLE_ARN` on the **`insolvia-staging`
environment**. Not the repository: the role's trust policy only accepts tokens
minted for that environment, so a repo-level secret would be a value no job
could use.

**The test user's Cognito `sub`.** Step 2 sets this as `E2E_TEST_USER_SUBJECT`
alongside the other two — it reads the value from the pool rather than asking,
so there is nothing extra to run and nothing to retype. It is not a credential,
but it identifies a person, so it stays out of committed files.

The fixture names its person by subject rather than resolving one at seed time
because the pipeline holds **no `cognito-idp` grant at all**: `ci-trust` is
applied before any pool exists, so it cannot scope `AdminGetUser` to staging,
and the wildcard would reach prod's pool and its customer addresses.

If you set up staging before this existed, re-run step 2 — it is an upsert, and
`--check` tells you which of the three are missing.

### What step 1 should print

The pool id it read from Terraform, then `Created.`, then a permanent-password
line, then:

```
[ ok ] E2E user is ready in us-east-1_XXXXXXXXX (CONFIRMED, no MFA).
```

`CONFIRMED` is the checkpoint. A user left in `FORCE_CHANGE_PASSWORD` makes the
hosted UI serve a "set a new password" screen that the browser test cannot
answer, so the E2E job would hang until its timeout.

### What step 2 should print

A ✓ against both `E2E_TEST_USER_EMAIL` and `E2E_TEST_USER_PASSWORD`, scoped to
the **`insolvia-staging` environment** — not the repository. Repo-level secrets
would be visible to every workflow; environment secrets are visible only to a
job that declares `environment: insolvia-staging`, which is what the `e2e` job
in `app-staging.yml` does (which release.yml calls as its last staging leg).

### Verify without changing anything

Both scripts take `--check` and exit non-zero if their half is not in place:

```bash
./scripts/staging-aws-create-test-user.sh --check
./scripts/staging-github-set-secrets.sh --check
```

## Then: prove it end to end

Trigger a staging deploy — merge to `main`, or dispatch **App · Deploy ·
Staging** — and watch the `e2e` job, which runs last, after the S3 sync and the
CloudFront invalidation.

Green means a real Chromium signed in against real staging, came back through
`/auth/callback`, and saw the test user's own email address rendered by the app.
That last assertion is the one that matters: the address only reaches the screen
if the token exchange and the `/v1/me` call both worked.

## Done when

- Both `--check` commands pass.
- One `App · Deploy · Staging` run has a green `e2e` job.
- A production promotion of that commit is no longer blessed by a deploy that
  asserted nothing.

## When it goes wrong

The E2E job's failure output names what it expected. The three failures worth
recognising on sight:

| Symptom | Cause |
|---|---|
| Config load fails naming `E2E_TEST_USER_EMAIL` or `E2E_TEST_USER_PASSWORD` | The secret is unset, **or** the job lost its `environment: insolvia-staging` key — an environment-scoped secret resolves to an empty string in silence when the environment is missing or borrowed (`infra/CLAUDE.md`). |
| `"Sign in" should redirect to the Cognito hosted UI` | The build shipped without a usable `EXPO_PUBLIC_COGNITO_DOMAIN` / `EXPO_PUBLIC_COGNITO_CLIENT_ID` pair. The build step guards against them being *empty*, so look for a wrong value: a pool recreated since the last apply. |
| `the hosted UI should redirect back to /auth/callback` | Cognito matches callback URLs **exactly**. The app client no longer lists this origin's `/auth/callback` — check `web_origins` in `infra/envs/staging/main.tf` and the route's location in the app. |
| The job hangs, then times out at the sign-in form | The test user is in `FORCE_CHANGE_PASSWORD`, or has enrolled a TOTP factor. MFA is `OPTIONAL` on the pool, so enrolment is possible and it is fatal to this flow. Re-run step 1; never enrol MFA on this account. |

**Rotating the password** is a re-run of both scripts with the new value, in the
same order. Step 2 warns before overwriting.

**Do not add this to the required status checks on `main`.** It needs a deployed
environment, so as a merge gate it would put staging's availability on every
PR's critical path — see `docs/reference/architecture.md` § Required status
checks, and the `insolvia-branch-protection` skill if the required set genuinely
needs to change.
