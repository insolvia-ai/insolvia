# Staging E2E — one-time setup

**State:** open — actionable now. **Step 1 must be re-run** for the seed role's
case-table and document-bucket grants ([ADR 0021](../adr/0021-test-tiers-and-seed-fixtures.md)),
and step 3 (publishing the seed fixture) is new. Three steps by hand, once;
after that the pipeline provisions its own test data on every staging deploy.

Two post-deploy suites sign in against real staging: the browser suite in
[`e2e/`](../../e2e/README.md) through the Cognito hosted UI, and the API's
integration tier in [`services/api/tests/integration/`](../../services/api/tests/integration/)
by SRP. A signed-in user is useless to either without a **firm** — a case
belongs to one (ADR 0009), so a user in none resolves to no accessor and every
route behind `current_accessor()` answers 403 — and the specs that touch a case
with documents in it need one to exist.

**All of that is the pipeline's job.** `.github/actions/seed-staging` (run by
`api-staging.yml` and `app-staging.yml`) loads
[`../../seeds/staging.json`](../../seeds/staging.json) before each suite: it
creates each account in the pool, sets its password, puts it in the firm the
fixture names, and converges the fixture case — rows in the case table and its
sample documents copied server-side out of the shared fixture bucket —
idempotently, every deploy. A pool or a table recreated tomorrow is restored by
the next run rather than found a week later as a mystery app regression.

What is left for a human is what CI cannot grant itself: **one IAM apply, one
secret, and one publish of the fixture bytes.**

Why it is worth doing: production only ships behind a green staging stage of
`release.yml` (in-run via `needs`, or via the `insolvia/staging-release`
commit status for a hand-dispatched deploy) — so the strength of the production
gate is exactly the strength of what the staging run asserts. See issue #40 for
the shape of the suite and #80 for the first test.

## Adding a test user is not in this runbook

It is an edit to [`../../seeds/staging.json`](../../seeds/staging.json), and
nothing else — no script, no secret, no change to `e2e/support/env.ts`. The
next deploy creates the account and seeds the membership.

That matters because the tenancy model is *about* several people with different
reach. `may_see_case`, `access_all_cases`, another firm's case answering 404
rather than 403, and the 409 that stops a firm removing its last administrator
are all untestable with a single account. The fixture ships with three people
across two firms for exactly that reason.

The addresses are committed on purpose: every one ends in `.test`, a reserved
TLD (RFC 2606) that can never be a real mailbox — which is what
`e2e/CLAUDE.md`'s no-addresses rule protects against — and nothing is mailed to
them anyway. **A real mailbox must never appear there.**

## Before you start

- A **staging-capable AWS session**. If it is not working, read the
  `insolvia-aws-auth` skill; the credential mechanics live there.
- A `gh` login with write access to the repository's Actions secrets.
- A password you generate yourself, meeting the pool policy: **12+ characters
  with a lower-case letter, an upper-case letter and a digit** (symbols allowed,
  not required — `infra/modules/auth/main.tf`). One value, shared by every
  seeded account: they are throwaway identities in an environment with no
  customer data, and one password that rotates cleanly beats one per person
  that rotates by hand.

**The repo is public.** The password goes into no file here, no commit message,
no PR description. The script reads it from your environment or prompts without
echo.

## The order

### 1. Let the pipeline into the staging pool, the case table and the buckets

The seed role (`insolvia-staging-seed-role`) needs to create accounts in the
staging pool, write the firm and case tables, copy fixture objects into
staging's case-documents bucket, and read the shared fixture bucket. The
table and bucket grants are in `ci-trust` already (`StagingCaseTableRows`,
`StagingCaseDocumentObjects`, `StagingCaseKeyThroughS3Only`,
`DevFixtureObjectsRead`) and only need the apply. The pool's ARN is not
knowable when `ci-trust` is first applied — a pool ARN contains a generated
id — so it is passed in as a variable once staging exists.

**CI cannot apply this root.** `DenySelfPrivilegeEscalation` means an apply run
as the deploy role fails by design; see the `insolvia-deploy-role-permissions`
skill. A human runs:

```bash
terraform -chdir=infra/envs/staging output -raw auth_user_pool_arn
```

Put that value in `infra/envs/ci-trust/terraform.tfvars` as
`staging_user_pool_arn`, then:

```bash
./scripts/apply-ci-trust.sh
```

Expect added statements on the seed role's policy (and the deploy role's
`DevFixtureBucket` statement), and nothing destroyed or replaced. Until this
apply, the seed step fails on `AccessDenied` for the first grant it lacks and
the staging stage fails with it — that is this step, not a regression.

Also set `AWS_SEED_ROLE_ARN` on the **`insolvia-staging` environment** if it is
not already there — `terraform -chdir=infra/envs/ci-trust output -raw
github_seed_role_arn`. Environment-scoped, not repository: the role's trust
policy only accepts tokens minted for that environment, so a repo-level secret
would be a value no job could use.

### 2. The password

```bash
./scripts/staging-github-set-secrets.sh          # prompts, without echo
```

One secret, `E2E_TEST_USER_PASSWORD`, on the `insolvia-staging` environment.
Re-running rotates it, and the next deploy resets every seeded account to the
new value — so a rotation converges rather than locking the suite out.

**Why the environment and not the repository:** a repository secret is visible
to every workflow; an environment secret only to a job that declares
`environment: insolvia-staging`. The flip side is the trap `infra/CLAUDE.md`
warns about — a job that forgets the `environment:` key sees an empty string in
silence.

### Verify without changing anything

```bash
./scripts/staging-github-set-secrets.sh --check
```

### 3. Publish the seed fixture's bytes

The fixture case's sample documents are committed under
[`../../seeds/fixtures/v1/`](../../seeds/fixtures/v1/) and loaded from the
account's shared fixture bucket (`infra/modules/dev_fixtures`, applied by CI as
part of `envs/shared` — the first release after it merges creates the bucket).
CI never writes that bucket; a developer publishes, once per version:

```bash
./scripts/dev-fixture.sh publish v1
```

Idempotent by digest — re-running it is free, and `--check` says what would
upload. Until this has run, the seed step fails naming the missing object.

## Then: prove it end to end

Trigger a staging deploy — merge to `main`, or dispatch **API · Deploy ·
Staging** and **App · Deploy · Staging** — and watch the `integration` job and
the `e2e` job. The seed step runs before each suite, so a provisioning problem
fails there with a named error rather than as a mystery assertion afterwards.

Green means a real Chromium signed in against real staging, came back through
`/auth/callback`, and saw the test user's own email address rendered by the app.
That last assertion is the one that matters: the address only reaches the screen
if the token exchange and the `/v1/me` call both worked.

## Done when

- `./scripts/staging-github-set-secrets.sh --check` passes.
- `./scripts/dev-fixture.sh publish v1 --check` reports nothing to upload.
- One `API · Deploy · Staging` run has a green `integration` job and one
  `App · Deploy · Staging` run has a green `e2e` job.
- A production promotion of that commit is no longer blessed by a deploy that
  asserted nothing.

## When it goes wrong

The seed step and the E2E job both name what they expected. The failures worth
recognising on sight:

| Symptom | Cause |
|---|---|
| Seed step: `AccessDenied` on `cognito-idp:AdminCreateUser` | Step 1 has not been done, or `staging_user_pool_arn` is empty in `ci-trust` — the grant is conditional on it, so the statement is simply absent. |
| Seed step: `AccessDenied` on a `dynamodb:*` call against `insolvia-staging-cases`, on `s3:PutObject`, or on `s3:GetObject` from the fixture bucket | Step 1 has not been re-applied since the case grants were added to `ci-trust`. |
| Seed step: `refusing: … the copy of objects/… did not land` or `NoSuchKey` | Step 3 has not been done for this fixture version, or `envs/shared` has not applied the bucket yet. |
| Integration: `could not sign in as 'admin': … NotAuthorized` | The password secret and the pool disagree — a rotation that only half landed — or the account is not CONFIRMED. If the seed step was green, suspect the former. |
| Seed step: `refusing: fixture references ${E2E_TEST_USER_PASSWORD}` | The secret is unset, **or** the job lost its `environment: insolvia-staging` key — an environment-scoped secret resolves to an empty string in silence when the environment is missing or borrowed (`infra/CLAUDE.md`). |
| Seed step: `InvalidPasswordException` | The stored password no longer meets the pool policy. Re-run step 2. |
| Suite: `No user with handle 'x' in …/seeds/staging.json` | A spec names a handle the fixture does not define. Add the person to the fixture, or fix the handle. |
| `"Sign in" should redirect to the Cognito hosted UI` | The build shipped without a usable `EXPO_PUBLIC_COGNITO_DOMAIN` / `EXPO_PUBLIC_COGNITO_CLIENT_ID` pair. The build step guards against them being *empty*, so look for a wrong value: a pool recreated since the last apply. |
| `the hosted UI should redirect back to /auth/callback` | Cognito matches callback URLs **exactly**. The app client no longer lists this origin's `/auth/callback` — check `web_origins` in `infra/envs/staging/main.tf` and the route's location in the app. |
| The job hangs, then times out at the sign-in form | The account has enrolled a TOTP factor. MFA is `OPTIONAL` on the pool, so enrolment is possible and it is fatal to this flow. Never enrol MFA on a seeded account. (`FORCE_CHANGE_PASSWORD` no longer causes this — the seeder sets a permanent password on every run.) |

**Rotating the password** is a re-run of step 2. The next deploy converges every
seeded account onto the new value.
