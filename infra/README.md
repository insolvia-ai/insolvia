# infra

Terraform for this repo's AWS infrastructure — the product's environments. See
[`../docs/reference/terraform.md`](../docs/reference/terraform.md) for the
model.

```
modules/                 web_hosting, marketing_site, auth, api_service, case_store, …
envs/staging/            staging-app / staging-www + the service stacks
envs/prod/               app / www + the service stacks (owns the apex)
envs/dev/                per developer machine (scripts/dev-aws-*)
```

The account-wide layer — `shared` (Route53 zone `insolvia.ai`, `*.insolvia.ai`
ACM cert, SES domain identity, every service's container repository),
`ci-trust` (OIDC provider + `insolvia-shared-deploy-role` + its policy,
human-applied only) and `account-access` (human IAM users and groups) — and the
mailer's modules live in
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services)
· [ADR 0025](../docs/adr/0025-shared-infra-and-mailer-move-to-insolvia-services.md).
One-time bootstrap is its
[`docs/runbooks/aws-bootstrap.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/aws-bootstrap.md); CI can't
apply its own permissions (the **insolvia-deploy-role-permissions** skill).

State: `s3://insolvia-shared-terraform-state-us-east-1`, key `insolvia/<env>/terraform.tfstate`.

## Usage

```bash
cd envs/<env>
terraform init
terraform plan     # offline validate: terraform init -backend=false && terraform validate
terraform apply
```

Apply order: `ci-trust` (human) → `shared` → `staging` / `prod`, where the
first two are applied from insolvia-services. DNS is live and deploys are real;
never `destroy` `shared` before the environments here that depend on its
zone/cert.
