# Developer / CI toolchain scripts

Idempotent bootstrap scripts that install the tools needed to build and test
this monorepo. Safe to re-run: every tool is checked before install, so an
already-installed dependency is never reinstalled. **Homebrew is the installer
on both macOS and Linux.**

Two layers — a shared base plus thin per-package scripts:

| Script | Scope | Does |
|---|---|---|
| `scripts/dev-setup.sh` | Shared base (all packages) | Terraform, tflint, AWS CLI, jq, Node.js (>= 24), FVM + the `.fvmrc`-pinned Flutter, Melos, Python 3.12 (+ Docker check) |
| `scripts/github-packages-auth.sh` | Shared base (npm consumers) | Ensures a `read:packages` token is available as `NODE_AUTH_TOKEN` so `npm ci` can install `@insolvia-ai/design-system` from GitHub Packages |
| `scripts/dev-aws-setup.sh` | Optional AWS layer | Provisions this machine's isolated dev resources (`infra/envs/dev`: waitlist table + Cognito pool) and wires `services/api/.env` at them; `--check` verifies |
| `scripts/dev-aws-reset.sh` | Optional AWS layer | Wipes this machine's dev **data** (table delete + recreate, Cognito users) — resources survive; `--dry-run`, `--skip-cognito` |
| `scripts/dev-aws-destroy.sh` | Optional AWS layer | `terraform destroy` of this machine's dev resources + unwinds `services/api/.env`; the machine id is retained |
| `scripts/dev-aws-common.sh` | Optional AWS layer (sourced) | Machine-UUID identity, per-machine state key, `aws configure export-credentials` helper shared by the three scripts above |
| `apps/insolvia_marketing/scripts/dev-setup.sh` | Marketing site | Shared base → packages auth → `npm ci`; `dev-up.sh` runs the dev server |
| `apps/insolvia_app/scripts/dev-setup.sh` | Flutter app | Shared base → workspace `fvm flutter pub get` at the repo root; `dev-up.sh` runs `fvm flutter run` |
| `packages/insolvia_design_system/scripts/dev-setup.sh` | Flutter design system | Shared base → **standalone** `fvm flutter pub get` in the package (it is outside the pub workspace, on purpose) |
| `packages/insolvia_design_system_react/scripts/dev-setup.sh` | React design system | Shared base → `npm ci`; `dev-up.sh` runs Storybook |
| `services/api/scripts/dev-setup.sh` | API service | Shared base → Python 3.12 venv at `services/api/.venv` + pinned deps; `dev-up.sh` runs the compose stack (API + dynamodb-local), `dev-test.sh` runs ruff + pytest exactly as CI does |

`packages/insolvia_tokens` and `packages/insolvia_api_client` have no scripts,
deliberately: they are pub workspace members with no setup beyond the workspace
resolve the app's script already performs, so a script there would be a third
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
- **Terraform**, **tflint**, and **FVM** are not in homebrew-core; the scripts
  install them from taps (`hashicorp/tap/terraform`,
  `terraform-linters/tap/tflint`, `leoafarias/fvm/fvm`) on every platform.
- **Flutter** is never installed directly — `fvm install` at the repo root
  reads the pin from `.fvmrc`, so bumping Flutter is a one-file change.
- **Melos** is pinned in the root `pubspec.yaml` (`melos: ^6.3.0`) and
  activated globally with `fvm dart pub global activate melos`; make sure
  `~/.pub-cache/bin` is on `PATH`.
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

## Per-machine AWS development resources (opt-in)

Local dev needs **zero AWS** by default — `services/api`'s compose stack runs
against dynamodb-local. The `dev-aws-*` scripts add an optional layer for
developing against real AWS: each developer machine gets its **own** isolated
resources (a waitlist DynamoDB table and a Cognito pool from
`infra/envs/dev/`), named with a persistent per-machine id so two developers
can never collide. It is deliberately **not** chained into any `dev-setup.sh`
— run it only when you want it:

```bash
./scripts/dev-aws-setup.sh --profile insolvia   # provision + wire services/api/.env
./scripts/dev-aws-setup.sh --check              # verify state, resources, env file
./services/api/scripts/dev-up.sh                # compose now hits YOUR real table
./scripts/dev-aws-reset.sh                      # wipe the data, keep the resources
./scripts/dev-aws-destroy.sh                    # tear it all down (machine id kept)
```

How it works:

- **Identity** — a UUID generated once into `~/.config/insolvia/machine-id`;
  its first 12 hex chars suffix every resource name
  (`insolvia-waitlist-dev-<short-id>`, `insolvia-users-dev-<short-id>`) and
  this machine's own Terraform state key
  (`insolvia/dev/<account-id>/<machine-id>/terraform.tfstate`).
- **Credentials** — your own AWS profile (default `insolvia`; `--profile` /
  `AWS_PROFILE` override). The scripts run
  `aws configure export-credentials` before every Terraform call — the
  `insolvia` profile uses the new `aws login` session format Terraform's SDK
  cannot read, so the export is required, not cosmetic. The same short-lived
  set is what `dev-up.sh` injects into the API container; credentials are
  never written to a file.
- **Wiring** — setup upserts `services/api/.env` (gitignored), which docker
  compose reads for `${VAR:-default}` substitution in
  `services/api/docker-compose.yml`; `dev-up.sh` sees `AWS_PROFILE` in it and
  switches the container from dynamodb-local to the real table. Destroy
  removes those keys, restoring the zero-AWS default.
- **Safety** — reset/destroy refuse to touch anything whose name does not
  match this machine's expected names, require a typed `RESET` (or `--yes`),
  and support `--dry-run`. CI never touches `infra/envs/dev` beyond offline
  `terraform validate`.

## Adding a new package

Give each package with real setup needs its own `<package>/scripts/dev-setup.sh`
for stack-specific steps (following `services/api/scripts/dev-setup.sh`), and
keep cross-cutting tools in the shared `scripts/dev-setup.sh`. A package whose
only "setup" is the workspace resolve does not get a script.
