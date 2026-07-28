# infra

Terraform for all Insolvia AWS infrastructure. See
[`../docs/TERRAFORM_ARCHITECTURE.md`](../docs/TERRAFORM_ARCHITECTURE.md) for the
model and [`../docs/AWS_SETUP.md`](../docs/AWS_SETUP.md) for one-time bootstrap.

```
modules/                 web_hosting, artifact_hosting, marketing_site, email, api_service, mailer_service, …
envs/ci-trust/           OIDC provider + insolvia-github-actions deploy role + its policy (human-applied only)
envs/shared/             Route53 zone insolvia.ai, *.insolvia.ai ACM cert, SES domain identity + mail DNS
envs/staging/            staging-app / staging-www / staging-download + the service stacks
envs/prod/               app / www / download + the service stacks (owns the apex)
```

State: `s3://insolvia-terraform-state`, key `insolvia/<env>/terraform.tfstate`.
The deploy role lives in **`ci-trust`**, not `shared` — CI can't apply its own
permissions (see [`../docs/AWS_SETUP.md`](../docs/AWS_SETUP.md) and the
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
its zone/cert.
