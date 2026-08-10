# infra — agent rules

All AWS infrastructure. Human overview: [`README.md`](README.md). Deep model:
[`docs/reference/terraform.md`](../docs/reference/terraform.md); one-time
bootstrap: [`docs/runbooks/aws-bootstrap.md`](../docs/runbooks/aws-bootstrap.md). Applies touch AWS — see
the `insolvia-aws-auth` skill first if credentials aren't working.

- **Terraform `>= 1.10`** (native S3 state locking via `use_lockfile`; no
  DynamoDB lock table), AWS provider `~> 6.37` — **6.37 is a deliberate floor,
  not a stale `~> 6.0`**. Two things set it, and the floor is the later:
  `aws_cognito_managed_login_branding` first shipped in 6.12.0, and
  `modules/case_store`'s `global_secondary_index.key_schema` first shipped in
  6.29.0 — but *working* `key_schema` is 6.37.0, because 6.32.1 fixed a
  perpetual diff on an index that has a range key (ours does) and 6.37.0 fixed
  a removal that deletes **every** index on the table. Bump the floor whenever
  a root starts depending on a newer resource or a data-loss fix. All six roots
  carry the same pin. **Region `us-east-1` everywhere** (CloudFront ACM
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
- **Naming `insolvia-<thing>-<env>`;** tags `{ Project = "insolvia",
  Environment, ManagedBy = "terraform" }`. Sensitive vars `sensitive = true`,
  never committed — commit `terraform.tfvars.example`, never real `*.tfvars`.
  **Carve-out: resources owned by `shared` carry no `-<env>` suffix**, because
  they genuinely have no environment — `insolvia-api`, `insolvia-marketing`,
  `insolvia-mailer` (the container repositories). Do not "fix" those names.
- **Container repositories are shared across environments**, one per service,
  in `envs/shared`. This is what lets a prod deploy run the exact image digest
  staging validated instead of rebuilding — see the note in
  `envs/shared/main.tf`. One-repo-per-env is deliberately rejected; environment
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
- **The `account-access` root** holds the **human** IAM users and groups —
  `scripts/apply-account-access.sh`, human-applied. CI cannot apply it: the
  deploy role has no `iam:*User*`/`iam:*Group*` action at all, deliberately, so
  merged code can never mint an admin. **Do not add MFA devices, login profiles
  or access keys to it** — all three write the credential into the state
  bucket; `docs/reference/terraform.md` § "Human account access" has the table
  and `docs/runbooks/iam-mfa-rotation.md` the procedure that replaces them. Do
  not add a self-service "manage your own MFA" policy either: every user is in
  `Admin`, so it grants nothing that isn't already held.
- **The GitHub org login is lowercase `insolvia-ai`.** GitHub emits the stored
  casing in the OIDC `sub`, and the IAM `StringLike` condition is case-sensitive
  — a mismatch fails `AssumeRoleWithWebIdentity` with an unhelpful error. Keep it
  lowercase everywhere.
- **Never `terraform apply` staging or prod from the CLI.** Those deploy in CI:
  staging on merge to `main`, prod by approving the release run's `promote`
  gate in the GitHub UI. The only legitimate local applies are your own dev
  env (`scripts/dev-aws-*`) and the human-gated `ci-trust`. See the
  `insolvia-deploy` skill.
- **One pipeline, both environments.** `release.yml` (push to `main`) runs the
  staging stage, parks at the `insolvia-production` approval, then runs the
  production stage — each stage orchestrating reusable `*-<env>.yml` service
  workflows with `needs`. **No service workflow applies Terraform** —
  `infra-staging.yml` and `infra-prod.yml` are the only appliers of their
  roots, and the service legs only read outputs. **Infra is the first job of
  both stages**, because both sets of service legs read its outputs; the
  staging apply is ungated (staging *is* `main`), the prod apply sits behind
  the release's approval for the exact commit staging just validated.
  `docs/reference/architecture.md` owns the full comparison.
- **Before a large or risky change, dispatch the env's infra workflow with
  `mode: plan` and read it.** Both default to `plan`. `shared-infra-plan.yml`
  only validates offline (`init -backend=false`), so it catches syntax and type
  errors but never shows what a change would *do*. Fine to skip for a routine
  change; not for a provider major-version bump or anything that might replace
  a resource.
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
