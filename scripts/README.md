# Developer / CI toolchain scripts

Idempotent bootstrap scripts that install the tools needed to build and test
this monorepo. Safe to re-run: every tool is checked before install, so an
already-installed dependency is never reinstalled. **Homebrew is the installer
on both macOS and Linux.**

Two layers — a shared base plus thin per-package scripts:

| Script | Scope | Does |
|---|---|---|
| `scripts/dev-setup.sh` | Shared base (all packages) | Terraform, tflint, AWS CLI, jq, Node.js (>= 24), Watchman, Python 3.12 (+ Docker check) |
| `scripts/github-packages-auth.sh` | Shared base (npm consumers) | Ensures a `read:packages` token is available as `NODE_AUTH_TOKEN` so `npm ci` can install `@insolvia-ai/design-system` from GitHub Packages |
| `scripts/dev-aws-setup.sh` | Per-machine AWS layer | Provisions this machine's isolated dev resources (`infra/envs/dev`: waitlist table + Cognito pool) and wires `services/api/.env` at them; `--check` verifies |
| `scripts/dev-aws-reset.sh` | Per-machine AWS layer | Wipes this machine's dev **data** (table delete + recreate, Cognito users) — resources survive; `--dry-run`, `--skip-cognito` |
| `scripts/dev-aws-destroy.sh` | Per-machine AWS layer | `terraform destroy` of this machine's dev resources + unwinds `services/api/.env`; the machine id is retained |
| `scripts/dev-aws-common.sh` | Per-machine AWS layer (sourced) | Machine-UUID identity, per-machine state key, `aws configure export-credentials` helper shared by the three scripts above and `dev-up.sh` |
| `scripts/prod-deploy.sh` | Deploys (not setup) | Dispatches a production `workflow_dispatch` workflow with `gh`; `--list`, `--ref`, `--input`, `--yes`, `--no-watch`. Target `release` ships one commit to every service in order |
| `scripts/bootstrap-ecr-images.sh` | One-time env bootstrap | Seeds the ECR image(s) an environment's Image-package Lambdas need before Terraform can create them (the first-apply deadlock documented in `infra/modules/*/main.tf`); `<env> [api\|mailer\|marketing …] [--dispatch] [--yes]` |
| `scripts/update-ruleset.sh` | Repo protection | Adds/removes a required status check on the `protect-main` ruleset — `show`, `add "<name>"`, `remove "<name>"`. Read-modify-write, because the ruleset `PUT` replaces whatever array you send it. See the `insolvia-branch-protection` skill. |
| `scripts/e2e-create-test-user.sh` | Staging E2E setup (one-time) | Creates the dedicated test user in the **staging** Cognito pool (self-signup is disabled, so `admin-create-user` is the only path) and gives it a permanent password so the first sign-in is not a password-change challenge. Pool id from `terraform output`, never a literal; password from the environment or a no-echo prompt, never a file. `--check`. Needs a staging AWS session — see the `insolvia-aws-auth` skill. |
| `scripts/e2e-set-secrets.sh` | Staging E2E setup (one-time) | Sets `E2E_TEST_USER_EMAIL` / `E2E_TEST_USER_PASSWORD` as **`insolvia-staging` environment** secrets (the same scope as `AWS_ROLE_ARN`, not repo-level), read from the environment and piped on stdin. Re-running rotates, and says so first. `--check`, `--yes`. |
| `scripts/apply-ci-trust.sh` | Human-gated trust apply | Applies `infra/envs/ci-trust` (OIDC provider + deploy role + its policy) — the one root CI can't apply (`DenySelfPrivilegeEscalation`). Credential dance + plan review + confirm. Use when a deploy fails on an IAM `AccessDenied` after you granted the pipeline a new permission. See `docs/runbooks/aws-bootstrap.md` § "The ci-trust anchor". |
| `apps/insolvia_marketing/scripts/dev-setup.sh` | Marketing site | Shared base → packages auth → `npm ci`; `dev-up.sh` runs the dev server |
| `apps/insolvia_app/scripts/dev-setup.sh` | Expo app | Shared base → npm workspace install at the repo root; `dev-up.sh` starts the Expo dev server |
| `services/api/scripts/dev-setup.sh` | API service | Shared base → Python 3.12 venv at `services/api/.venv` + pinned deps → chains into `scripts/dev-aws-setup.sh` (forwards `--profile`/`--region`/`--yes`/`--check`); `dev-up.sh` runs the compose stack against this machine's real AWS table, `dev-test.sh` runs ruff + pytest exactly as CI does |

`packages/insolvia_tokens` and `packages/insolvia_api_client` have no scripts,
deliberately: they are npm workspace members with no setup beyond the root
`npm ci` the app's script already performs, so a script there would be a third
name for the same command.

Every `dev-setup.sh` takes `--check` to report status without installing
anything; per-package scripts pass it through to the shared base.

## Targets (both use Homebrew)

- **macOS** (developer machines) — `brew` runs as your normal user. Docker
  Desktop and Homebrew itself are the only interactive/GUI steps.
- **Linux** (cloud sandbox / CI) — Homebrew **refuses to run as root**, and
  these environments are root, so the script installs Homebrew into the default
  prefix `/home/linuxbrew/.linuxbrew` **owned by the non-root `ubuntu` user**
  and runs every `brew` call as that user via `sudo -u ubuntu`. The prefix
  `bin` is put on `PATH` for the current run and for future shells via
  `/etc/profile.d/homebrew.sh`, so root and CI agents can execute the tools.

Notes:
- **Terraform** and **tflint** are not in homebrew-core; the scripts install
  them from taps (`hashicorp/tap/terraform`, `terraform-linters/tap/tflint`) on
  every platform.
- **Watchman** is what Metro (the Expo/React Native bundler) uses to watch the
  source tree. Without it Metro falls back to Node's own recursive `fs`
  watching, which is slower and runs into the macOS open-file limit on a tree
  this size.
- **Expo itself is not a global tool.** It is an npm dependency of
  `apps/insolvia_app`, run through `npx expo` / that app's own scripts, so the
  version is pinned by the committed lockfile rather than by whatever a machine
  installed globally. Nothing to set up here beyond Node.
- **Xcode and CocoaPods are deliberately NOT installed.** The app is a web SPA
  today and nothing in this repo compiles native code. Mobile is deferred, not
  abandoned: `apps/insolvia_app/{ios,android}` are generated by `expo prebuild`
  (gitignored — see `.gitignore`), and that step is what re-introduces the
  native toolchain. Add it back to `dev-setup.sh` when the first prebuild
  lands, not before.
- **Python 3.12** matches `services/api` (pyproject `requires-python` and the
  `public.ecr.aws/lambda/python:3.12` base image); the venv itself is created
  by the service's script, not the shared base.

## Usage

```bash
# From the repo root — complete per-package setup in dependency order:
./services/api/scripts/dev-setup.sh
./apps/insolvia_marketing/scripts/dev-setup.sh

# Check every layer without installing anything:
./apps/insolvia_marketing/scripts/dev-setup.sh --check

# The shared package-neutral layer remains directly runnable:
./scripts/dev-setup.sh
```

On Linux, if `brew`/its tools aren't on your `PATH` in a fresh non-login shell:

```bash
eval "$(/home/linuxbrew/.linuxbrew/bin/brew shellenv)"
```

## GitHub Packages auth (`@insolvia-ai/design-system`)

`apps/insolvia_marketing` depends on `@insolvia-ai/design-system` published to
`npm.pkg.github.com`. Its committed `.npmrc` reads the token from
`${NODE_AUTH_TOKEN}`, and GitHub Packages requires a token with the
**`read:packages`** scope (classic PAT) / **Packages: Read-only** permission
(fine-grained PAT) for *every* read — even though the package is public. The
default `GH_TOKEN` in CI/sandboxes does not have it, so `npm ci` fails with a
401/403.

`scripts/github-packages-auth.sh` resolves this idempotently:

```bash
# CI / sandbox: provide a read:packages PAT, then the script picks it up:
export GITHUB_PACKAGES_TOKEN=<pat-with-read:packages>
eval "$(./scripts/github-packages-auth.sh --export)"   # sets NODE_AUTH_TOKEN

# Developer machine with gh: adds the scope to your existing login:
./scripts/github-packages-auth.sh                       # runs `gh auth refresh -s read:packages`

# Verify only (no changes):
./scripts/github-packages-auth.sh --check
```

The script never writes a token into a committed file — the repo is public,
and the `.npmrc` uses the `${NODE_AUTH_TOKEN}` env indirection. The only
non-scriptable step is creating a token with the scope in the first place (a
GitHub UI / `gh` action); the script does everything after that.

## Per-machine AWS development resources

The `dev-aws-*` scripts provision **local development's database** — there is
no local emulator (`services/api`'s compose stack talks straight to real AWS).
Each developer machine gets its **own** isolated
resources (a waitlist DynamoDB table and a Cognito pool from
`infra/envs/dev/`), named with a persistent per-machine id so two developers
can never collide. `services/api/scripts/dev-setup.sh` chains into setup
unconditionally, so the usual flow is just that script; the layer's own
commands are:

```bash
./scripts/dev-aws-setup.sh                      # provision + wire services/api/.env
./scripts/dev-aws-setup.sh --check              # verify state, resources, env file
./services/api/scripts/dev-up.sh                # compose against YOUR real table
./scripts/dev-aws-reset.sh                      # wipe the data, keep the resources
./scripts/dev-aws-destroy.sh                    # tear it all down (machine id kept)
```

The in-memory waitlist store still exists in code — it is what unit tests and
the bare `development_server` use when `WAITLIST_TABLE_NAME` is unset — but it
is a test seam, not the dev path: `dev-up.sh` refuses to start until setup has
written `services/api/.env`.

How it works:

- **Identity** — a UUID generated once into `~/.config/insolvia/machine-id`;
  its first 12 hex chars suffix every resource name
  (`insolvia-waitlist-dev-<short-id>`, `insolvia-users-dev-<short-id>`) and
  this machine's own Terraform state key
  (`insolvia/dev/<account-id>/<machine-id>/terraform.tfstate`).
- **Credentials** — your own AWS profile (the `default` profile; `--profile` /
  `AWS_PROFILE` override if your Insolvia session lives elsewhere). The scripts
  run `aws configure export-credentials` before every Terraform call — the
  profile uses the new `aws login` session format Terraform's SDK cannot read,
  so the export is required, not cosmetic. The same short-lived set is what
  `dev-up.sh` injects into the API container; credentials are never written to
  a file.
- **Wiring** — setup upserts `services/api/.env` (gitignored), which docker
  compose reads for `${VAR:-default}` substitution in
  `services/api/docker-compose.yml`; `dev-up.sh` reads `AWS_PROFILE` from it
  to export credentials and requires `WAITLIST_TABLE_NAME` to be present.
  Destroy removes those keys, so `dev-up.sh` fails fast until the next setup.
- **Safety** — reset/destroy refuse to touch anything whose name does not
  match this machine's expected names, require a typed `RESET` (or `--yes`),
  and support `--dry-run`. CI never touches `infra/envs/dev` beyond offline
  `terraform validate`.

## Production deploys (`prod-deploy.sh`)

Every production workflow is `workflow_dispatch`-only — nothing reaches prod on
a push to `main`. (The one auto-apply on `main` is `shared-infra-deploy.yml`,
and it applies `infra/envs/shared` only.) `prod-deploy.sh` is that dispatch
button on the command line.

```bash
# What ran last against each production target, and how it went:
./scripts/prod-deploy.sh --list

# Infrastructure only — plan is the default, and it is read-only:
./scripts/prod-deploy.sh prod-infra
./scripts/prod-deploy.sh prod-infra --input mode=apply

# Deploy a service — prompts, then follows the run to completion:
./scripts/prod-deploy.sh api

# Ship one specific commit rather than main's HEAD:
./scripts/prod-deploy.sh api --input sha=abc1234

# Ship one commit to every service, in dependency order:
./scripts/prod-deploy.sh release

# Fire and forget:
./scripts/prod-deploy.sh --yes --no-watch app
```

Targets are `release`, `prod-infra`, `api`, `mailer`, `app`, `marketing`, and
`shared-infra`.

**A service deploy promotes; it does not rebuild.** The container repositories
are shared across environments, so the image staging validated already sits in
the repository prod pulls from — the workflow resolves that commit's
`sha-<commit>` tag to an immutable digest and deploys it. It also refuses to
run unless that exact commit has a successful staging deploy; `--input
force=true` bypasses the check for a hotfix and says so loudly in the job
summary. (The app rebuilds, because it bakes its environment into the bundle at
build time — the `EXPO_PUBLIC_*` values are inlined by Metro, so staging and
prod are genuinely different bundles rather than one artifact promoted twice.)

**Use `prod-infra` for infrastructure changes — it is the only way.**
`infra/envs/prod` is a single root module with a single state, so
`terraform apply` there reconciles *all* of it. The service workflows apply no
Terraform; they only read outputs — so a service deploy can neither carry an
infra-only change while redeploying a service you never meant to touch, nor
drag unrelated infra drift into production alongside a routine code deploy.
`infra-prod.yml` does the apply and stops.

It defaults to `mode: plan`, which is read-only and writes the plan to the run's
job summary. That is the only way to see a plan against real prod state:
`shared-infra-plan.yml` validates every env offline (`init -backend=false`, no
credentials), so PR CI can never produce one.

It needs a `gh` login and **no AWS credentials** — every deploy authenticates
to AWS inside the workflow via OIDC. The script only dispatches; it never
applies anything locally.

What it adds over clicking *Run workflow* in the UI:

- **Shows the commit GitHub will actually build**, resolved from the remote ref
  rather than your checkout, and warns when your local `HEAD` differs from it
  or you have uncommitted changes. Dispatching while the change is still
  unpushed is the usual way to be surprised by a deploy.
- **Warns on a non-`main` `--ref`**, since infra applies are meant to run from
  merged `main`.
- **Warns before the known red herring**: `marketing-prod.yml` ends by
  smoke-testing `https://www.insolvia.ai/`, so while the site is parked
  (`site_enabled = false` in `infra/envs/prod`) that run goes red even though
  its apply succeeded.
- **Exits non-zero on a failed run**, so it chains with `&&`.

Whichever target you pick, the apply is `-auto-approve` and covers the whole
env, so accumulated drift is reconciled along with your change. Run
`prod-infra` in its default plan mode first if that matters.

## Staging E2E setup (`e2e-*.sh`)

Two one-time scripts that give the post-deploy auth round trip in
`.github/workflows/app-staging.yml` something to sign in as. Run them in this
order, once; the order, the expected output and how to tell it worked are in
[`../docs/runbooks/staging-e2e-setup.md`](../docs/runbooks/staging-e2e-setup.md).

```bash
export E2E_TEST_USER_EMAIL='…'      # a dedicated synthetic address, never a real mailbox
./scripts/e2e-create-test-user.sh   # prompts for the password, without echo
./scripts/e2e-set-secrets.sh        # same two values → the insolvia-staging environment

./scripts/e2e-create-test-user.sh --check
./scripts/e2e-set-secrets.sh --check
```

Neither script accepts, writes, or generates a password into a file: this repo
is public, and the value exists only in your shell and in GitHub's encrypted
secret store. The test user must never enrol MFA — the pool allows it
(`mfa_configuration = "OPTIONAL"`), and a TOTP challenge is something a browser
test cannot answer, so the E2E job would hang and redden staging.

## Adding a new package

Give each package with real setup needs its own `<package>/scripts/dev-setup.sh`
for stack-specific steps (following `services/api/scripts/dev-setup.sh`), and
keep cross-cutting tools in the shared `scripts/dev-setup.sh`. A package whose
only "setup" is the workspace resolve does not get a script.
