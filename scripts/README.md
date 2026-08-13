# Developer / CI toolchain scripts

Idempotent bootstrap scripts that install the tools needed to build and test
this monorepo. Safe to re-run: every tool is checked before install, so an
already-installed dependency is never reinstalled. **Homebrew is the installer
on both macOS and Linux.**

Two layers — a shared base plus thin per-package scripts:

| Script | Scope | Does |
|---|---|---|
| `scripts/dev-setup.sh` | Shared base (all packages) | Terraform, tflint, AWS CLI, jq, Node.js (>= 24), Watchman, Python 3.12 (+ Docker check), **and the agent skills in `.agents/skills/`** — installed from `skills-lock.json`, not committed (see below) |
| `scripts/dev-up.sh` | Whole system | Brings the API, mailer, app and marketing site up together in one terminal by delegating to each area's own `dev-up.sh`; prefixed logs, and one Ctrl-C that runs every `dev-down.sh`. Takes no arguments — to run one part, run that part's own script |
| `scripts/dev-down.sh` | Whole system | Stops everything `dev-up.sh` starts — containers included — for when Ctrl-C never got the chance: a closed terminal, a killed process, or a stack started from **another checkout** (ports and compose project names are machine-global, so one machine runs one stack). Delegates to each area's `dev-down.sh`; idempotent, and also what `dev-up.sh`'s own Ctrl-C trap runs |
| `scripts/github-packages-auth.sh` | Shared base (npm consumers) | Ensures a `read:packages` token is available as `NODE_AUTH_TOKEN` so `npm ci` can install `@insolvia-ai/design-system` from GitHub Packages |
| `scripts/dev-aws-setup.sh` | Per-machine AWS layer | Provisions this machine's isolated dev resources (`infra/envs/dev`: waitlist table + Cognito pool) and wires `services/api/.env` **and `apps/insolvia_app/.env`** at them; `--check` verifies |
| `scripts/dev-aws-seed.sh` | Per-machine AWS layer | Makes this machine signable-in **and** seeded, in one run. First ensures the dev account exists in this machine's Cognito pool — there is **no sign-up screen** on any pool (`allow_admin_create_user_only`), so this is the only way to get one; the password comes from `~/.config/insolvia/dev.env`, `DEV_USER_PASSWORD`, or a no-echo prompt that offers to write that file. Then loads [`seeds/dev.json`](../seeds/dev.json) into this machine's dev tables — today a firm with your dev account as its **admin**. Without the firm a signed-in developer resolves to no firm and every route behind `current_accessor()` answers **403** (`no_active_firm_user`), because the first firm cannot come from the API (`POST /v1/firm/users` is itself behind `FIRM_ADMINISTRATION`). **Edit the fixture to change what is seeded; this script only says where.** The same loader puts `seeds/staging.json` into staging from `app-staging.yml`, so the two environments differ in fixture and tables, never in the code path that built the rows — which is why the account step lives in this wrapper, not the loader. `--check` verifies both halves |
| `scripts/dev-aws-reset.sh` | Per-machine AWS layer | Wipes this machine's dev **data** — waitlist, case, access-log and firm tables (delete + recreate) plus the pool's users — resources survive; `--dry-run`, `--skip-cognito`. Leaves you needing `dev-aws-seed.sh` again, which recreates the account and the firm in one run |
| `scripts/dev-aws-destroy.sh` | Per-machine AWS layer | `terraform destroy` of this machine's dev resources + unwinds both `.env` files; the machine id is retained |
| `scripts/dev-aws-destroy-orphan.sh` | Per-machine AWS layer | `terraform destroy` of a **previous** machine-id's leftovers — `<short-id>` from the orphaned resource names; finds that id's own state key in the bucket, destroys everything the state tracks, then deletes the state object |
| `scripts/dev-aws-common.sh` | Per-machine AWS layer (sourced) | Machine-UUID identity, per-machine state key, `aws configure export-credentials` helper shared by the four scripts above and `dev-up.sh` |
| `scripts/bootstrap-ecr-images.sh` | One-time env bootstrap | Seeds the ECR image(s) an environment's Image-package Lambdas need before Terraform can create them (the first-apply deadlock documented in `infra/modules/*/main.tf`); `<env> [api\|mailer\|marketing …] [--dispatch] [--yes]` |
| `scripts/update-ruleset.sh` | Repo protection | Adds/removes a required status check on the `protect-main` ruleset — `show`, `add "<name>"`, `remove "<name>"`. Read-modify-write, because the ruleset `PUT` replaces whatever array you send it. See the `insolvia-branch-protection` skill. |
| `scripts/staging-github-set-secrets.sh` | Staging E2E setup (one-time) | Sets `E2E_TEST_USER_PASSWORD` as an **`insolvia-staging` environment** secret (narrower than `AWS_ROLE_ARN`, which is repo-level — the seed role's trust policy only accepts tokens minted for that environment, so a repo-level secret would be a value no job could use), read from the environment or a no-echo prompt and piped on stdin. **The only secret staging's test data needs** — the addresses are in [`seeds/staging.json`](../seeds/staging.json) and the accounts are created by the seed step in `app-staging.yml`, so adding a test user comes nowhere near this script. Re-running rotates, and the next deploy converges every seeded account onto the new value. `--check`, `--yes`. |
| `scripts/apply-ci-trust.sh` | Human-gated trust apply | Applies `infra/envs/ci-trust` (OIDC provider + deploy role + its policy) — the one root CI can't apply (`DenySelfPrivilegeEscalation`). Credential dance + plan review + confirm. Use when a deploy fails on an IAM `AccessDenied` after you granted the pipeline a new permission. See `docs/runbooks/aws-bootstrap.md` § "The ci-trust anchor". |
| `scripts/apply-account-access.sh` | Human-gated IAM apply | Applies `infra/envs/account-access` (the human IAM users, their groups, their attached policies). Same credential dance + plan review + confirm, plus guards for the two ways this root can lock you out. Use when someone joins, leaves or changes group. **Not** for rotating your own MFA — that is `docs/runbooks/iam-mfa-rotation.md`, and no Terraform resource is involved on purpose. |
| `scripts/migrate-state-bucket.sh` | One-time, human | Copies Terraform state from the old `insolvia-terraform-state` bucket to `insolvia-shared-terraform-state-us-east-1` (the naming refactor's one non-conforming bucket). **Copies and verifies key-for-key; never deletes the source** — losing state does not lose data, it loses the ability to destroy what the state described. `--check` diffs the two buckets and changes nothing. Prints the exact follow-up order, including the two GitHub secrets that must move to the renamed role ARNs or every deploy fails at `configure-aws-credentials`. |
| `scripts/rename-teardown.sh` | One-time, per env | Clears what would make the naming-rename apply fail half-way: DynamoDB deletion protection, Cognito deletion protection, every object **and version** in the six buckets, and the images in the ECR repositories. `<staging\|prod> [--check] [--yes]`. **This destroys data — on prod, real case documents and every user's password.** `--check` reports without touching anything; prod additionally requires typing the word `prod`. Run it *before* the rename apply, then re-seed images with `bootstrap-ecr-images.sh`. |
| `apps/insolvia_marketing/scripts/dev-setup.sh` | Marketing site | Shared base → packages auth → `npm ci`; `dev-up.sh` runs the dev server |
| `apps/insolvia_admin/scripts/dev-setup.sh` | Admin portal | Same shape as marketing (own lockfile, packages auth → `npm ci`); `dev-up.sh` runs Vite on the pinned port 3100 |
| `apps/insolvia_app/scripts/dev-setup.sh` | Expo app | Shared base → npm workspace install at the repo root; `dev-up.sh` starts the Expo dev server |
| `services/admin/scripts/dev-setup.sh` | Admin service | Shared base → venv + pinned deps; `dev-up.sh` runs the compose stack on 8090 (in-memory without `services/admin/.env`), `dev-test.sh` runs ruff + mypy + pytest exactly as CI does |
| `e2e/scripts/dev-test.sh` | E2E, against local dev | Runs the Playwright suite against `http://localhost:3000` and **this machine's** dev Cognito pool, instead of deployed staging. Needs `scripts/dev-up.sh` running and `scripts/dev-aws-seed.sh` done; the password comes from `E2E_TEST_USER_PASSWORD` if exported, else `~/.config/insolvia/dev.env` — no committed default, the repo is public. The address comes from [`seeds/dev.json`](../seeds/dev.json). `--headed` to watch it. The staging run in `app-staging.yml` is unchanged and stays authoritative |
| `services/api/scripts/dev-setup.sh` | API service | Shared base → Python 3.12 venv at `services/api/.venv` + pinned deps → chains into `scripts/dev-aws-setup.sh` (forwards `--profile`/`--region`/`--yes`/`--check`); `dev-up.sh` runs the compose stack against this machine's real AWS table, `dev-test.sh` runs ruff + pytest exactly as CI does |

`packages/insolvia_api_client` has no scripts, deliberately: it is an npm
workspace member with no setup beyond the root `npm ci` the app's script
already performs, so a script there would be a second name for the same
command.

Every `dev-setup.sh` takes `--check` to report status without installing
anything; per-package scripts pass it through to the shared base.

## The agent skills are installed, not committed

`.agents/skills/` and the symlinks in `.claude/skills/` are **gitignored**, so a
fresh clone has none of them until `scripts/dev-setup.sh` runs. The tracked file
is `skills-lock.json` — the manifest of which skills come from which source —
and the installer is [`skills`](https://github.com/vercel-labs/skills), run via
`npx` (no global install; Node is the only requirement).

They were committed once: 131 files of third-party documentation in the tree,
carried through reviews as if we owned it. The lock is the part worth tracking.

Two things to know:

- **The layout matters.** dev-setup installs with
  `--agent universal --agent claude-code`, which puts the real directory at
  `.agents/skills/<name>/` and symlinks `.claude/skills/<name>` to it. Targeting
  claude-code alone *copies* into `.claude/skills/` and creates no `.agents/`
  tree — which every path in `CLAUDE.md` and the ADRs would then miss.
- **It can leave `skills-lock.json` dirty.** `skills add` takes each source at
  its current HEAD; the CLI has no command that restores the exact hashes the
  lock records. So the step is reproducible in *which* skills you get, not in
  their content, and a changed lock means an upstream skill moved. Read that
  diff rather than discarding it.

To add one, install it and commit the resulting lock change:

```bash
npx skills add <owner/repo> --skill <name> --agent universal --agent claude-code -y
```

Root [`CLAUDE.md`](../CLAUDE.md) carries the applicability table — which of the
installed skills this repo actually follows, and which it deliberately ignores.

## Where a script goes, and what it is called

Two rules, written down because both were guessed at once and got it wrong.

**Location — who owns the thing it acts on.** This directory provisions
*environments and the repo*; an area's own `scripts/` builds, runs or tests
*that area*. Nothing here runs any area's test suite, which is why the
Playwright runner is `e2e/scripts/dev-test.sh` while the two scripts that
provision staging for it are here. The test is not "is this about e2e?" but
"does this act on the e2e package, or on an environment?"

**Name — for what it acts on, never for who needs it.** These two were once
`e2e-create-test-user.sh` and `e2e-set-secrets.sh`, named after their consumer,
which read as though they had been left in the wrong directory. That is the
whole rule, and it is the only part of it that is absolute.

The optional parts are an **environment** and a **system** token, in the order
`<environment>-<system>-<verb>`. Include one when it disambiguates and leave it
out when it does not — this directory is not on a single mechanical scheme and
should not be forced onto one. `github-packages-auth.sh` and
`staging-github-set-secrets.sh` both earn their `github`, because "packages"
could be npm's and "secrets" could be AWS Secrets Manager's, and in this repo
both of those are real. `update-ruleset.sh` needs no such token: GitHub is the
only thing here with rulesets. `dev-up.sh` names no system because it runs all
of them, and `apply-ci-trust.sh` reads `<verb>-<environment>` because the
environment *is* the object. A token that resolves no ambiguity is noise, and a
scheme applied for its own sake produces `github-update-ruleset.sh`.

`dev-aws-*` is a step stronger than the scheme: those scripts share
`dev-aws-common.sh` (machine-id identity, the per-machine state key, the
credential export), so the prefix marks family membership, not just a target.
A script that does not source it should not borrow the prefix —
`bootstrap-ecr-images.sh` deliberately re-implements the credential dance for a
non-per-machine target, and says so where it does it.

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

## Running the whole system (`scripts/dev-up.sh`)

```bash
./scripts/dev-up.sh     # api + admin + mailer + app + admin portal + marketing.
                        # Ctrl-C stops it all.
```

That is the whole interface — it takes no arguments. To run one part, run that
part's own script instead; there is no reason for this one to grow a flag that
duplicates them.

| Service | URL | Up | Down |
|---|---|---|---|
| app | <http://localhost:3000> | `apps/insolvia_app/scripts/dev-up.sh` | `…/dev-down.sh` |
| api | <http://127.0.0.1:8080/health> | `services/api/scripts/dev-up.sh` | `…/dev-down.sh` |
| admin api | <http://127.0.0.1:8090/health> | `services/admin/scripts/dev-up.sh` | `…/dev-down.sh` |
| admin portal | <http://localhost:3100> | `apps/insolvia_admin/scripts/dev-up.sh` | `…/dev-down.sh` |
| marketing | <http://localhost:5173> | `apps/insolvia_marketing/scripts/dev-up.sh` | `…/dev-down.sh` |
| mailer | <http://127.0.0.1:8026> (Mailpit <http://127.0.0.1:8025>) | `services/mailer/scripts/dev-up.sh` | `…/dev-down.sh` |

The portal's port matters the same way the app's does: the dev Google OAuth
client registers `http://localhost:3100` as an exact origin, which is why its
`dev-up.sh` runs Vite with `--strictPort` rather than letting it drift. Its
sign-in needs no local account — staff use their own `@insolvia.ai` Google
Workspace login.

The two admin halves mirror their hostnames (`admin-api.insolvia.ai` /
`admin.insolvia.ai`): **8090 is the admin service, a JSON API** — `/health` is
its only browser-friendly URL and everything else 404s or wants a Google
token — and **the browsable staff UI is the portal on 3100**.

**Every area owns both halves.** `dev-up.sh` knows how to start that area — the
API's exports short-lived AWS credentials before `compose up`, the app's pins
port 3000 because Cognito registers that exact origin. `dev-down.sh` knows how
to stop it, which is not the same as killing the process that started it:
compose containers outlive their `up`, and a stray `npx` grandchild keeps
holding 3000. The root script delegates to both and re-implements neither.

Each `dev-down.sh` is idempotent and safe to run when nothing is up — teardown
that can fail is teardown you stop trusting. When a previous session left
things held — a closed terminal, a killed process, or a stack started from a
different checkout — `./scripts/dev-down.sh` runs all four at once; it is the
same code path `dev-up.sh`'s own Ctrl-C trap uses, so the clean exit and the
recovery cannot drift apart. To stop just one area, run that area's script.

Two things worth knowing:

- **You need an account and a firm before the app is usable, and one script
  makes both.** The hosted UI has no sign-up link and is not going to grow one
  — every pool sets `allow_admin_create_user_only`, because a public sign-up
  form on a bankruptcy-filing platform is an invitation to junk accounts. And
  an account only gets you past sign-in: the account lives in Cognito, firm
  membership lives in DynamoDB, and without the firm `/v1/me` reports no firm
  and everything else answers 403 — which reads as a broken build rather than
  a missing step. `./scripts/dev-aws-seed.sh` does both in order. The first
  run prompts for a password (no echo) and offers to save it to
  `~/.config/insolvia/dev.env` (chmod 600, outside the repo tree — this repo
  is public and an in-tree `.env` has been committed here once already); with
  the file in place, later runs and `e2e/scripts/dev-test.sh` need no prompt
  and no export. `dev-aws-reset.sh` deletes the pool's users and recreates
  the firm table — so re-run the seed script after every reset.
- **It needs `dev-aws-setup.sh` to have run.** There is no DynamoDB emulator
  and no fake Cognito: the API talks to this machine's real per-developer
  tables and the app signs in against its real pool. Preflight says so by name
  rather than letting a container fail obscurely.
- **The first run of the day is slow.** Both compose stacks build images and
  Vite does its cold dependency scan, concurrently. The readiness wait is sized
  for that; a service still missing at the end says so and keeps trying.

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
./scripts/dev-aws-destroy-orphan.sh <short-id>  # tear down a PREVIOUS machine-id's leftovers
```

A lost or regenerated `~/.config/insolvia/machine-id` orphans that old id's
environment: `dev-aws-destroy.sh` only ever inits the *current* id's state
key, so the old resources and state object survive every teardown.
`dev-aws-destroy-orphan.sh` takes the 12-char short id visible in the leftover
resource names (e.g. `insolvia-dev-<short-id>-waitlist`), locates that id's
own state object in the bucket, destroys everything the state tracks, and
removes the state object. It refuses the current machine's id, and without
`--yes` the embedded `terraform destroy` shows its plan and asks first.

The in-memory waitlist store still exists in code — it is what unit tests and
the bare `development_server` use when `WAITLIST_TABLE_NAME` is unset — but it
is a test seam, not the dev path: `dev-up.sh` refuses to start until setup has
written `services/api/.env`.

How it works:

- **Identity** — a UUID generated once into `~/.config/insolvia/machine-id`;
  its first 12 hex chars suffix every resource name
  (`insolvia-dev-<short-id>-waitlist`, `insolvia-dev-<short-id>-users`) and
  this machine's own Terraform state key
  (`insolvia/dev/<account-id>/<machine-id>/terraform.tfstate`).
- **Credentials** — your own AWS profile (the `default` profile; `--profile` /
  `AWS_PROFILE` override if your Insolvia session lives elsewhere). The scripts
  run `aws configure export-credentials` before every Terraform call — the
  profile uses the new `aws login` session format Terraform's SDK cannot read,
  so the export is required, not cosmetic. The same short-lived set is what
  `dev-up.sh` injects into the API container; credentials are never written to
  a file.
- **Wiring** — setup upserts two gitignored `.env` files, and there is
  deliberately **no `.env.example` for either**: every value is an identifier
  for a resource `infra/envs/dev` creates per machine, so a copied template
  would name nothing. Run setup and it writes them.
  - `services/api/.env` — docker compose reads it for `${VAR:-default}`
    substitution in `services/api/docker-compose.yml`; `dev-up.sh` reads
    `AWS_PROFILE` from it to export credentials and requires
    `WAITLIST_TABLE_NAME`. `AUTH_ISSUER_URL`/`AUTH_CLIENT_ID` point token
    verification at this machine's pool — the API fails **closed**, so without
    them every protected route answers 401.
  - `apps/insolvia_app/.env` — Expo loads it automatically and inlines the
    `EXPO_PUBLIC_*` values at build time. Without
    `EXPO_PUBLIC_COGNITO_DOMAIN`/`_CLIENT_ID` the app renders "sign-in is not
    configured" rather than failing loudly, so a missing file here is easy to
    mistake for the app simply not having sign-in yet. Both point at the same
    pool the API verifies against, so a local sign-in mints a token this
    machine's own API accepts.

  Destroy removes those keys, so `dev-up.sh` fails fast and the app returns to
  its unconfigured state until the next setup.
- **Safety** — reset/destroy refuse to touch anything whose name does not
  match this machine's expected names, require a typed `RESET` (or `--yes`),
  and support `--dry-run`. CI never touches `infra/envs/dev` beyond offline
  `terraform validate`.

## Production deploys have no script

There used to be a `prod-deploy.sh` here. It is gone on purpose: production
ships through the release pipeline — merge to `main`, staging deploys, and
approving the run's `promote` gate in the GitHub UI ships production. The
emergency paths (single-service `*-prod.yml`, `infra-prod.yml` plan/apply) are
plain `workflow_dispatch` in the Actions UI or `gh workflow run`. The
`insolvia-deploy` skill owns the full picture.

## Staging E2E setup (`staging-*.sh`)

One one-time script that gives the post-deploy auth round trip in
`.github/workflows/app-staging.yml` something to sign in as. The expected output
and how to tell it worked are in
[`../docs/runbooks/staging-e2e-setup.md`](../docs/runbooks/staging-e2e-setup.md).

```bash
./scripts/staging-github-set-secrets.sh            # prompts for the password, without echo
./scripts/staging-github-set-secrets.sh --check    # verify, change nothing
```

The accounts themselves are created by the seed step in `app-staging.yml` from
[`../seeds/staging.json`](../seeds/staging.json), so **adding a test user is an
edit to that fixture** — no script, no extra secret. The other human step is a
one-off `apply-ci-trust.sh` that lets the seed role into the staging pool; the
runbook has it.

The script neither writes nor generates a password into a file: this repo is
public, and the value exists only in your shell and in GitHub's encrypted secret
store. A seeded account must never enrol MFA — the pool allows it
(`mfa_configuration = "OPTIONAL"`), and a TOTP challenge is something a browser
test cannot answer, so the E2E job would hang and redden staging.

## Adding a new package

Give each package with real setup needs its own `<package>/scripts/dev-setup.sh`
for stack-specific steps (following `services/api/scripts/dev-setup.sh`), and
keep cross-cutting tools in the shared `scripts/dev-setup.sh`. A package whose
only "setup" is the workspace resolve does not get a script.
