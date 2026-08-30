# infra

Terraform for all Insolvia AWS infrastructure. See
[`../docs/reference/terraform.md`](../docs/reference/terraform.md) for the
model and [`../docs/runbooks/aws-bootstrap.md`](../docs/runbooks/aws-bootstrap.md) for one-time bootstrap.

```
modules/                 web_hosting, marketing_site, auth, email, api_service, mailer, …
envs/ci-trust/           OIDC provider + insolvia-shared-deploy-role deploy role + its policy (human-applied only)
envs/account-access/     human IAM users + groups + their attached policies (human-applied; CI has no IAM user/group permissions at all)
envs/shared/             Route53 zone insolvia.ai, *.insolvia.ai ACM cert, SES domain identity + mail DNS
envs/staging/            staging-app / staging-www + the service stacks
envs/prod/               app / www + the service stacks (owns the apex)
```

State: `s3://insolvia-shared-terraform-state-us-east-1`, key `insolvia/<env>/terraform.tfstate`.
The deploy role lives in **`ci-trust`**, not `shared` — CI can't apply its own
permissions (see [`../docs/runbooks/aws-bootstrap.md`](../docs/runbooks/aws-bootstrap.md) and the
**insolvia-deploy-role-permissions** skill).

## Usage

```bash
cd envs/<env>
terraform init
terraform plan     # offline validate: terraform init -backend=false && terraform validate
terraform apply
```

Apply order: `ci-trust` (human) → `shared` → `staging` / `prod`. DNS is live and
deploys are real; never `destroy` `shared` before the environments that depend on
its zone/cert. `account-access` sits outside that order — nothing depends on it;
apply it (`scripts/apply-account-access.sh`) when the people change. Rotating an
MFA device is not an apply — see
[`../docs/runbooks/iam-mfa-rotation.md`](../docs/runbooks/iam-mfa-rotation.md).
