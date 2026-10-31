# infra — agent rules

This repo's AWS infrastructure: the `staging`, `prod` and per-machine `dev`
roots and the modules they call. Human overview: [`README.md`](README.md). Deep
model: [`docs/reference/terraform.md`](../docs/reference/terraform.md). Applies
touch AWS — see the `insolvia-aws-auth` skill first if credentials aren't
working.

**The account-wide layer is not here.** `shared` (zone, wildcard cert, SES
identity, every service's container repository, the fixture bucket),
`ci-trust` (the OIDC provider and the deploy role), `account-access` (the human
IAM users), the mailer's modules and **auth** (the Cognito pools, domains and
app clients — per-machine dev pools included) live in
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services),
the account-wide roots with the same state keys they had here; one-time
bootstrap is its
[`docs/runbooks/aws-bootstrap.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/aws-bootstrap.md) ·
[ADR 0025](../docs/adr/0025-shared-infra-and-mailer-move-to-insolvia-services.md) ·
[ADR 0026](../docs/adr/0026-auth-is-a-shared-service.md).

- **Terraform `>= 1.10`** (native S3 state locking via `use_lockfile`; no
  DynamoDB lock table), AWS provider `~> 6.37` — **6.37 is a deliberate floor,
  not a stale `~> 6.0`**. Two things set it, and the floor is the later:
  `aws_cognito_managed_login_branding` first shipped in 6.12.0 (it lives in
  insolvia-services now, which keeps the same pin), and
  `modules/case_store`'s `global_secondary_index.key_schema` first shipped in
  6.29.0 — but *working* `key_schema` is 6.37.0, because 6.32.1 fixed a
  perpetual diff on an index that has a range key (ours does) and 6.37.0 fixed
  a removal that deletes **every** index on the table. Bump the floor whenever
  a root starts depending on a newer resource or a data-loss fix. All three
  roots here carry the same pin, and so do insolvia-services' roots — keep
  them moving together. **Region `us-east-1` everywhere** (CloudFront ACM
  requirement).
- **Each root's `.terraform.lock.hcl` is committed** — the constraint above is
  the floor, the lock is the exact provider build every machine and CI run
  installs. To move to a newer provider, regenerate the lock for **both**
  platforms and commit the diff:
  `terraform -chdir=infra/envs/<env> providers lock -platform=linux_amd64 -platform=darwin_arm64`
  (linux_amd64 is CI, darwin_arm64 is developer machines). Never take the lock
  a plain `terraform init -upgrade` writes: it records only the current
  platform, so the other one fails checksum verification at init. If init ever
  reports a lock/constraint conflict, fix the lock with that command — don't
  delete the file.
- **Naming `insolvia-<env>-<component>-<identifier>`** — environment SECOND,
  component named for what it *serves*. **Read the `insolvia-aws-naming` skill
  before creating or renaming any AWS resource**; it owns the per-resource-type
  patterns (S3's region suffix, `-role`, `-dlq`, `-oac`), the component-naming
  rule, and which renames destroy data. Tags `{ Project = "insolvia",
  Environment, ManagedBy = "terraform" }`. Sensitive vars `sensitive = true`,
  never committed — commit `terraform.tfvars.example`, never real `*.tfvars`.
  **`shared` is an environment, not an exemption**: account-wide resources take
  it in the env slot — `insolvia-shared-api` (a container repository),
  `insolvia-shared-deploy-role`. There was once a carve-out giving them no env
  segment at all; it is gone.
- **A rename is not just a rename — insolvia-services' `infra/envs/ci-trust`
  fences the deploy role by ARN pattern, and two of those patterns are
  controls.** Because env is the second segment, a component fence is a MID-STRING wildcard
  (`arn:aws:s3:::insolvia-*-marketing-*`, `alias/insolvia-*-cases`). Missing one
  does not fail the apply — `DenyCaseDataDecryption` and `DenyAuditLogErasure`
  simply stop matching, and the pipeline silently gains what the deny existed to
  remove. `ci-trust` is human-applied, so any rename touching a fenced family is
  a two-repo, two-apply change: a PR to insolvia-services and its human
  `ci-trust` apply first, then the env here.
- **Container repositories are shared across environments**, one per service,
  in insolvia-services' `infra/envs/shared`. This is what lets a prod deploy
  run the exact image digest staging validated instead of rebuilding — see the
  note in [its `main.tf`](https://github.com/insolvia-ai/insolvia-services/blob/main/infra/envs/shared/main.tf). One-repo-per-env is
  deliberately rejected; environment isolation lives in separate Lambdas,
  roles, tables, SSM namespaces and Cognito pools, not in separate image
  stores. **A new service's repository is a PR there, released there, before
  the PR here that deploys it** — the env root's `data "aws_ecr_repository"`
  hard-fails until it exists.
- **The backend runs in the VPC on Aurora** (ADR 0028, since 18.22). A
  backend Lambda takes `vpc_subnet_ids = module.network.private_subnet_ids`,
  `vpc_security_group_ids = [module.network.lambda_security_group_id]` and
  `db_cluster_resource_id = module.aurora.cluster_resource_id`, and calls
  `modules/vpc_lambda_access` for its role — the inline ENI policy and
  `rds-db:connect` as ONE Postgres role (a `db_username` literal the module
  also publishes as `insolvia-db-username`). **Never attach
  `AWSLambdaVPCAccessExecutionRole`**: ci-trust's `ServiceRolePolicyAttachment`
  admits only `AWSLambdaBasicExecutionRole`, and that condition is a control,
  not an oversight. A new Postgres role is a migrator change first
  (`backend/src/Insolvia.Migrator`'s `ServiceRoles` and `Grants.cs`), then its
  name in `vpc_lambda_access`'s validation. The migrator Lambda
  (`modules/migrator`) is the only reader of the RDS master secret; the deploy
  role is denied it. `dynamodb_grants` on `case_store`, `firm_store`,
  `filing_credentials` and `filing_worker` is TRANSITIONAL — false in staging
  and prod, the default in dev (which passes no role to grant) — and goes
  with the tables, in the table-deletion PR after the prod promote (18.23
  deleted the Python but not the tables). `docs/reference/terraform.md` § "The backend on .NET and
  Aurora" has the per-host table.
- **Structure:** `modules/<concern>/{main,variables,outputs}.tf`,
  `envs/<env>/{main,variables,providers,backend,outputs}.tf`. State:
  `s3://insolvia-shared-terraform-state-us-east-1`, key
  `insolvia/<env>/terraform.tfstate`, `encrypt = true`. The bucket is created by
  hand (insolvia-services' `docs/runbooks/aws-bootstrap.md`), not by Terraform —
  a root cannot create its own backend.
- **Environments** `staging`, `prod`, `dev` (per machine) — each a separate
  `envs/<env>/` dir with its own state key, **never** Terraform workspaces.
- **The `ci-trust` and `account-access` roots are in insolvia-services**, both
  human-applied from there (`scripts/apply-ci-trust.sh`,
  `scripts/apply-account-access.sh` in that repo). When an apply here hits an
  IAM `AccessDenied`, the fix is a PR there — skill
  `insolvia-deploy-role-permissions`.
- **The GitHub org login is lowercase `insolvia-ai`.** GitHub emits the stored
  casing in the OIDC `sub`, and the IAM `StringLike` condition is case-sensitive
  — a mismatch fails `AssumeRoleWithWebIdentity` with an unhelpful error. Keep it
  lowercase everywhere.
- **Never `terraform apply` staging or prod from the CLI.** Those deploy in CI:
  staging on merge to `main`, prod by approving the release run's `promote`
  gate in the GitHub UI. The only legitimate local apply in this repo is your
  own dev env (`scripts/dev-aws-*`); the human-gated `ci-trust` apply runs from
  insolvia-services. See the `insolvia-deploy` skill.
- **One pipeline, both environments.** `release.yml` (push to `main`) runs the
  staging stage, parks at the `insolvia-production` approval, then runs the
  production stage — each stage orchestrating reusable `*-<env>.yml` service
  workflows with `needs`. Shared infra and the mailer are not legs of it: they
  release from insolvia-services' own `release.yml`. **No service workflow
  applies Terraform** — `infra-staging.yml` and `infra-prod.yml` are the only
  appliers of their roots, and the service legs only read outputs. **Infra is the first job of
  both stages**, because both sets of service legs read its outputs; the
  staging apply is ungated (staging *is* `main`), the prod apply sits behind
  the release's approval for the exact commit staging just validated.
  `docs/reference/architecture.md` owns the full comparison.
- **Before a large or risky change, dispatch the env's infra workflow with
  `mode: plan` and read it.** Both default to `plan`. `shared-infra-plan.yml`
  (staging, prod, dev) only validates offline (`init -backend=false`), so it
  catches syntax and type errors but never shows what a change would *do*. Fine to skip for a routine
  change; not for a provider major-version bump or anything that might replace
  a resource.
- **Apply order (when a human bootstrap is legitimate): `ci-trust` (human) →
  `shared` → `staging`/`prod` — and the first two are in insolvia-services, so
  the order now spans repositories.** `shared` creates the `*.insolvia.ai` cert
  **and the container repositories**; the envs here look both up by name
  (`statuses = ["ISSUED"]`, `data "aws_ecr_repository"`), so `shared` first —
  those lookups hard-fail until it has applied.
- **The mailer is not in these roots any more.** Its staging/prod resources are
  owned by insolvia-services' `infra/envs/{staging,prod}`; this repo still
  writes the API's `MAILER_API_URL` SSM parameter, built from
  `var.mailer_subdomain` — the hostname is the contract between the repos.
  Never re-add `module "mailer"` here: the resources exist, owned by the other
  repo's state, and a second declaration would try to create them again.
- **Auth is read, not owned: `modules/auth_contract` reads the SSM contract
  `/insolvia/<env>/auth/*`** that insolvia-services publishes (pool id/ARN,
  web and portal client ids, sign-in domain, issuer, MCP client ids). A
  missing parameter fails the plan by name — the pool's root there has not
  applied. `modules/auth` is the consumer half staging and prod call: the
  reader plus `AdminCreateUser` grants on this repo's own roles. A developer
  machine's `envs/dev` calls the reader alone — it signs in against the ONE
  shared dev pool (`insolvia-dev-users`). Pool configuration — callback
  origins, the custom domain, deletion protection, a new client or contract
  key — **and the sign-in page's style** (its managed-login branding, since
  2026-10-12) is a PR there, released first. Keep `module "auth"` at that
  name: the grants never moved in state because their addresses did not
  change, and renaming the call would destroy and re-create them. The move
  itself is recorded
  in insolvia-services'
  [`docs/runbooks/auth-state-migration.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/auth-state-migration.md).
- **A deploy job must declare `environment:`** matching the Terraform env
  (`insolvia-staging|production` here; `insolvia-shared` is insolvia-services')
  — env-scoped secrets are invisible otherwise and resolve to empty strings
  silently. Never borrow another env's name.
- **Account facts:** dedicated AWS account `521762924626`; domain `insolvia.ai`
  (`staging-app` / `app` / `www`). DNS is delegated and live; deploys are real.
