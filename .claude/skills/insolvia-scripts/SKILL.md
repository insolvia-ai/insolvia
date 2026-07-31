---
name: insolvia-scripts
description: >-
  The catalogue of Insolvia's repo scripts and WHICH one to run WHEN — they are
  the project's tools, not incidental files. Use this the moment a task involves
  setting up or running any part of the monorepo locally, provisioning or
  wiping this machine's dev AWS resources, bootstrapping an environment, or
  deploying: "set up my dev environment", "get the API running", "reset/clear
  my dev database", "run the marketing site / Storybook / the app", "deploy to
  prod / staging", "seed the ECR image", "apply ci-trust", "auth to GitHub
  Packages", or any time you're about to hand-roll a Terraform/docker/npm command
  that a committed script already wraps. Reach for this BEFORE improvising shell
  — running the wrong thing by hand can create real AWS resources or skip a
  required step. Defers AWS-credential specifics to insolvia-aws-auth and
  ci-trust specifics to insolvia-deploy-role-permissions.
---

# Insolvia repo scripts — which one, when

Full details live in [`scripts/README.md`](../../../scripts/README.md); this is
the fast index so you pick the right tool instead of hand-rolling commands.
Every `dev-setup.sh` is idempotent and takes `--check` (report without
installing).

## First-time / toolchain setup

| Want to… | Run |
|---|---|
| Install the shared toolchain (Terraform, tflint, AWS CLI, jq, Node ≥24, Watchman, Python 3.12) | `scripts/dev-setup.sh` |
| Make a `read:packages` token available so `npm ci` can pull `@insolvia-ai/design-system` | `scripts/github-packages-auth.sh` |

## Run a package locally

Each package has a thin `scripts/dev-setup.sh` (bootstrap) + `dev-up.sh` (run):

| Package | Setup → Run |
|---|---|
| App (Expo/RN web SPA) | `apps/insolvia_app/scripts/dev-setup.sh` → `dev-up.sh` (`expo start --web`, pinned to **:3000** — Cognito registers that exact origin) |
| Marketing site | `apps/insolvia_marketing/scripts/dev-setup.sh` → `dev-up.sh` (RR7 SSR dev server) |
| API | `services/api/scripts/dev-setup.sh` → `dev-up.sh` (compose) → `dev-test.sh` (ruff+pytest, matches CI) |
| Mailer | `services/mailer/scripts/dev-setup.sh` → `dev-up.sh` (compose + Mailpit) → `dev-test.sh` |

`packages/insolvia_tokens`, `packages/insolvia_api_client` and
`packages/insolvia_design_system` deliberately have no scripts — the root
workspace install covers them, and the design system has no dev server of its
own (its `.web` leaves render in marketing, its `.native` leaves in the app).

## Per-machine dev AWS resources (the API's real dev DB — there is no emulator)

These touch real AWS, so read **insolvia-aws-auth** first if credentials aren't
already working.

| Want to… | Run |
|---|---|
| Provision this machine's isolated dev resources (`infra/envs/dev`: waitlist table + Cognito) and wire `services/api/.env` | `scripts/dev-aws-setup.sh` (`--check` verifies) |
| Wipe this machine's dev **data** (table delete+recreate, Cognito users); resources survive | `scripts/dev-aws-reset.sh` (`--dry-run`, `--skip-cognito`) |
| `terraform destroy` this machine's dev resources + unwind `.env` | `scripts/dev-aws-destroy.sh` |

(`dev-aws-common.sh` is sourced, not run.)

## Environment bootstrap & deploys

| Want to… | Run | Notes |
|---|---|---|
| Seed the ECR image an env's Image-package Lambdas need before Terraform can create them | `scripts/bootstrap-ecr-images.sh <env> [api\|mailer\|marketing] [--dispatch] [--yes]` | Breaks the documented first-apply deadlock |
| Dispatch a **production** deploy (prod is `workflow_dispatch`-only) | `scripts/prod-deploy.sh` (`--list`, `--ref`, `--input`, `--yes`, `--no-watch`) | Uses `gh`; watches the run. Promotes the staging-validated image rather than rebuilding, and refuses commits staging never shipped green. `release` chains every service |
| Apply `infra/envs/ci-trust` (OIDC provider + deploy role + policy) after a deploy fails on a newly-granted IAM permission | `scripts/apply-ci-trust.sh` | Human-gated; CI **cannot** apply this. See **insolvia-deploy-role-permissions** |
| Add / remove / show a required status check on `main`'s `protect-main` ruleset | `scripts/update-ruleset.sh [show\|add\|remove] "<check name>"` | Resolves the ruleset **by name**, never a hard-coded id, and re-PUTs the whole ruleset so it can't drop the other rules. Names must match a workflow job's `name:` exactly. See **insolvia-branch-protection** |

Staging deploys automatically on merge to `main` — there is no staging script.
