---
name: insolvia-deploy
description: >-
  How Insolvia ships — and the rule that deploys happen in CI, never from your
  CLI. Use this whenever a task is about deploying, shipping, releasing, or
  "applying" infrastructure/services to staging or prod: "deploy to prod",
  "ship the API", "apply the terraform", "push a new version live", "run
  terraform apply", "update the lambda", "release". Read it BEFORE running any
  `terraform apply`, `docker push` to ECR, `aws lambda update-function-code`, or
  `aws`/`gh` deploy command against staging or prod — those are CI's job, and
  doing them by hand bypasses the OIDC deploy role and the environment gates.
---

# Deploying Insolvia

**Deploys run in GitHub Actions, triggered by git — not from anyone's CLI.**
The only credential that touches staging/prod AWS is the CI OIDC deploy role.

## How each thing ships

| Target | Trigger |
|---|---|
| **staging** (api, app, mailer, marketing) | **automatic on merge to `main`** — each `*-staging.yml` has a `paths:` filter, so merging a change to that area deploys it |
| **shared infra** | automatic on merge to `main` (`shared-infra-deploy.yml`, paths `infra/envs/shared/**`) |
| **prod** (api, app, mailer, marketing) | **`workflow_dispatch` only** — dispatch via `./scripts/prod-deploy.sh` (uses `gh`; `--list`, `--ref`, `--yes`, `--no-watch`) |
| **prod infra** | `infra-prod.yml`, `workflow_dispatch` with `mode: plan` (default, read-only → job summary) or `mode: apply` |

So the deploy procedure is: **open a PR, get it merged to `main`** (staging ships
itself), and for prod, **dispatch the workflow** with `./scripts/prod-deploy.sh`.

## The rule

**Do not deploy or apply against staging or prod from the CLI.** Specifically,
never run against staging/prod:

- `terraform apply` in `infra/envs/staging` or `infra/envs/prod`
- `docker push` to a service ECR repo, or `aws lambda update-function-code`
- `aws s3 sync` of client assets or a CloudFront invalidation for a live env

If you want to *see* a prod plan, that is `infra-prod.yml` with `mode: plan`
(the only way to plan against real prod state — `shared-infra-plan.yml` validates
offline with no credentials).

## The only local applies that are legitimate

- **Your own per-machine dev env** — `scripts/dev-aws-*` against `infra/envs/dev`
  (that's your dev database; see the `insolvia-scripts` skill).
- **The `ci-trust` anchor** — `scripts/apply-ci-trust.sh`, human-gated because CI
  cannot apply its own permissions (see `insolvia-deploy-role-permissions`).

Everything else is a merge or a dispatch, never a hand-run apply.
