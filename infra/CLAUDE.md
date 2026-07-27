# infra — agent rules

All AWS infrastructure. Human overview: [`README.md`](README.md). Deep model:
[`docs/TERRAFORM_ARCHITECTURE.md`](../docs/TERRAFORM_ARCHITECTURE.md); one-time
bootstrap: [`docs/AWS_SETUP.md`](../docs/AWS_SETUP.md). Applies touch AWS — see
the `insolvia-aws-auth` skill first if credentials aren't working.

- **Terraform `>= 1.10`** (native S3 state locking via `use_lockfile`; no
  DynamoDB lock table), AWS provider `~> 5.0`. **Region `us-east-1` everywhere**
  (CloudFront ACM requirement).
- **Naming `insolvia-<thing>-<env>`;** tags `{ Project = "insolvia",
  Environment, ManagedBy = "terraform" }`. Sensitive vars `sensitive = true`,
  never committed — commit `terraform.tfvars.example`, never real `*.tfvars`.
  **Carve-out: resources owned by `shared` carry no `-<env>` suffix**, because
  they genuinely have no environment — `insolvia-api`, `insolvia-marketing`,
  `insolvia-mailer` (the container repositories). Do not "fix" those names.
- **Container repositories are shared across environments**, one per service,
  in `envs/shared`. This is what lets a prod deploy run the exact image digest
  staging validated instead of rebuilding — see the note in
  `envs/shared/main.tf`. It deliberately replaced one-repo-per-env; environment
  isolation lives in separate Lambdas, roles, tables, SSM namespaces and
  Cognito pools, not in separate image stores.
- **Structure:** `modules/<concern>/{main,variables,outputs}.tf`,
  `envs/<env>/{main,variables,providers,backend,outputs}.tf`. State:
  `s3://insolvia-terraform-state`, key `insolvia/<env>/terraform.tfstate`,
  `encrypt = true`.
- **Environments** `staging`, `prod`, `shared` (account-wide) — each a separate
  `envs/<env>/` dir with its own state key, **never** Terraform workspaces.
- **The `ci-trust` root** (OIDC provider + `insolvia-github-actions` deploy role
  + its policy) is applied by a **human, never CI** (`DenySelfPrivilegeEscalation`)
  — `scripts/apply-ci-trust.sh`; skill `insolvia-deploy-role-permissions`.
- **The GitHub org login is lowercase `insolvia-ai`.** GitHub emits the stored
  casing in the OIDC `sub`, and the IAM `StringLike` condition is case-sensitive
  — a mismatch fails `AssumeRoleWithWebIdentity` with an unhelpful error. Keep it
  lowercase everywhere.
- **Never `terraform apply` staging or prod from the CLI.** Those deploy in CI:
  staging on merge to `main`, prod via `workflow_dispatch` (`scripts/prod-deploy.sh`).
  The only legitimate local applies are your own dev env (`scripts/dev-aws-*`) and
  the human-gated `ci-trust`. See the `insolvia-deploy` skill.
- **Apply order (when a human bootstrap is legitimate): `ci-trust` (human) →
  `shared` → `staging`/`prod`.** `shared` creates the `*.insolvia.ai` cert
  **and the container repositories**; downstream envs look both up by name
  (`statuses = ["ISSUED"]`, `data "aws_ecr_repository"`), so `shared` first —
  those lookups hard-fail until it has applied.
- **A deploy job must declare `environment:`** matching the Terraform env
  (`insolvia-shared|staging|production`) — env-scoped secrets are invisible
  otherwise and resolve to empty strings silently. Never borrow another env's name.
- **Account facts:** dedicated AWS account `521762924626`; domain `insolvia.ai`
  (`staging-app` / `app` / `www`). DNS is delegated and live; deploys are real.
