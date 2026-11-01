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
| Install the shared toolchain (Terraform, tflint, AWS CLI, jq, Node ≥24, Watchman, Python 3, the .NET SDK `backend/global.json` pins) | `scripts/dev-setup.sh` |
| Make a `read:packages` token available so `npm ci` can pull `@insolvia-ai/design-system` | `scripts/github-packages-auth.sh` |

## Run everything at once

| Want to… | Run |
|---|---|
| Bring the whole system up in one terminal (the backend — Postgres, migrator, API :8080, admin :8090 — plus mailer, app, admin portal, marketing), prefixed logs, one Ctrl-C stops it all | `scripts/dev-up.sh` — **takes no arguments**. The mailer runs from a sibling insolvia-services checkout (or `$INSOLVIA_SERVICES_DIR`) and is skipped without one — the API then records mail in memory |
| Stop everything after a lost terminal or a killed dev-up, or reclaim ports/containers another **checkout**'s run left held | `scripts/dev-down.sh` (idempotent) |
| Stop one area only | that area's `scripts/dev-down.sh` (idempotent) |

`dev-up.sh` delegates to the per-area `dev-up.sh`/`dev-down.sh` pairs below
rather than duplicating them — which is also why it has no `--only`: to run one
part, run that part's own script. The database is local (Postgres in Docker);
sign-in is not — without `dev-aws-setup.sh` (which writes `backend/.env`) and
`dev-aws-seed.sh` everything starts but every signed-in route answers 401.

## Run a package locally

Each package has a thin `scripts/dev-setup.sh` (bootstrap) + `dev-up.sh` (run):

| Package | Setup → Run |
|---|---|
| App (Expo/RN web SPA) | `apps/insolvia_app/scripts/dev-setup.sh` → `dev-up.sh` (`expo start --web`, pinned to **:3000** — Cognito registers that exact origin) |
| Marketing site | `apps/insolvia_marketing/scripts/dev-setup.sh` → `dev-up.sh` (RR7 SSR dev server) |
| Backend (.NET: API, admin, and their Postgres) | `backend/scripts/dev-up.sh` (compose: Postgres :55432, the migrator, API :8080, admin :8090) → `backend/scripts/dev-down.sh`. One host by hand: `dotnet run --project backend/src/Insolvia.<Host>` (`backend/CLAUDE.md`) |
| Mailer | **In insolvia-services now** — `../insolvia-services/services/mailer/scripts/dev-setup.sh` → `dev-up.sh` (compose + Mailpit) → `dev-test.sh`, run in that checkout |

`packages/insolvia_api_client` deliberately has no scripts — the root workspace
install covers it.

## Test

Four tiers, each a directory, each aimed at fixed environments — the
`insolvia-testing` skill says how to write one, ADR 0021 says which runs where.

| Want to… | Run |
|---|---|
| Run every **unit** suite in the repo, or only those a set of files can break (what the **pre-push hook** runs; `--list` shows the mapping) | `scripts/dev-test-unit.sh [files…]` |
| The backend's full gate (exactly its PR check) | `cd backend && dotnet format Insolvia.slnx --verify-no-changes && dotnet test Insolvia.slnx` (Docker running: the data tier uses Testcontainers) |
| The **contract** suite: the .NET hosts on a throwaway Postgres, through the shipped api-client, with the client-coverage gate | `contract/scripts/dev-test.sh [jest path pattern or -t name]` |
| The **contract** suite's deployed target against this machine's local stack (HTTP, signed in by SRP; no AWS credentials) | `contract/scripts/dev-test.sh --target deployed [jest filter]` — needs `dev-up.sh` running and `dev-aws-seed.sh` done |
| The browser suite against this machine's dev stack — both projects, or `--project smoke` for the unauthenticated half | `e2e/scripts/dev-test.sh [--headed] [playwright args]` |
| The same tiers against **staging** or the smoke project against **production** | never from a laptop — `api-staging.yml`, `app-staging.yml` and `app-prod.yml` run them after each deploy |

The design system and the design tokens are **not in this repo** at all; they
live in `insolvia-ai/design-system` and install from GitHub Packages. If an
install here 401s on `@insolvia-ai/*`, that is registry auth, not a missing
setup script: run `./scripts/github-packages-auth.sh`.

## Per-machine dev AWS resources, the shared dev pool, and the local database

These touch real AWS, so read **insolvia-aws-auth** first if credentials aren't
already working.

| Want to… | Run |
|---|---|
| Provision this machine's isolated dev resources (`infra/envs/dev`: the bucket, the queues, the keys) and write `backend/.env` + `apps/insolvia_app/.env`. Sign-in is the ONE shared dev pool (`insolvia-dev-users`, insolvia-services', ADR 0026, read through SSM): setup stops naming the missing parameter if that pool was never applied (`scripts/apply-dev-auth.sh` in insolvia-services, once). On a machine set up before the move, the plan destroys the old per-machine pool — expected; run `dev-aws-seed.sh` after | `scripts/dev-aws-setup.sh` (`--check` verifies) |
| Get a usable signed-in dev environment: creates the dev sign-in account if missing — there is **no sign-up screen** on any pool (`allow_admin_create_user_only`) — and seeds its firm **and the fixture cases** `seeds/dev.json` names into the local Postgres, through the migrator. Also the fix for **"signed in, but every route 403s"**: the account exists in Cognito but belongs to no firm, and the first firm cannot come from the API because `POST /v1/firm/users` is itself behind `FIRM_ADMINISTRATION`. Password from `~/.config/insolvia/dev.env`, `DEV_USER_PASSWORD`, or a no-echo prompt that offers to write that file | `scripts/dev-aws-seed.sh` (`--check`) |
| Wipe this machine's dev **data** (the local database volume; `--documents` empties this machine's bucket too); resources survive, and so do Cognito accounts — the pool is shared by every machine. Leaves your account **without a firm** — re-run the row above | `scripts/dev-aws-reset.sh` (`--dry-run`) |
| Publish a committed seed-fixture version (`seeds/fixtures/<v>/`) into the account's shared fixture bucket, idempotently — once, after its PR merges. There is no `capture` since 18.23: a new version is curated by hand (`seeds/README.md`) | `scripts/dev-fixture.sh publish v1 [--check]` |
| `terraform destroy` this machine's dev resources + unwind `backend/.env` and the app's `.env` | `scripts/dev-aws-destroy.sh` |
| Destroy a **previous** machine-id's orphaned dev resources (leftovers a lost/regenerated `~/.config/insolvia/machine-id` strands, which destroy can't reach) | `scripts/dev-aws-destroy-orphan.sh <short-id>` |

(`dev-aws-common.sh` is sourced, not run.)

## Environment bootstrap & deploys

| Want to… | Run | Notes |
|---|---|---|
| Seed the ECR image an env's Image-package Lambdas need before Terraform can create them | `scripts/bootstrap-ecr-images.sh <env> [api\|admin\|worker\|marketing\|mcp\|filing\|migrator] [--dispatch] [--yes]` | Breaks the documented first-apply deadlock. Pushes images only — a missing *repository* is added in insolvia-services' `infra/envs/shared` |
| Apply `ci-trust` (OIDC provider + deploy role + policy) after a deploy fails on a newly-granted IAM permission | **Not here** — insolvia-services' `scripts/apply-ci-trust.sh`, run from that checkout | Human-gated; CI **cannot** apply this. See **insolvia-deploy-role-permissions** |
| Add / remove / show a required status check on `main`'s `protect-main` ruleset | `scripts/update-ruleset.sh [show\|add\|remove] "<check name>"` | Resolves the ruleset **by name**, never a hard-coded id, and re-PUTs the whole ruleset so it can't drop the other rules. Names must match a workflow job's `name:` exactly. See **insolvia-branch-protection** |

There is no deploy script at all: staging deploys automatically on merge to
`main`, and production ships by approving the release run's `promote` gate in
the GitHub UI. See the **insolvia-deploy** skill.
