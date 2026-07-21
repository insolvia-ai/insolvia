# infra

Terraform for all Insolvia AWS infrastructure. See
[`../docs/TERRAFORM_ARCHITECTURE.md`](../docs/TERRAFORM_ARCHITECTURE.md) for the
model and [`../docs/AWS_SETUP.md`](../docs/AWS_SETUP.md) for one-time bootstrap.

```
modules/web_hosting/   S3 (private+OAC) + CloudFront + Route53 alias for a Flutter-web SPA
modules/email/         SES domain identity, DKIM, custom MAIL FROM, apex MX/SPF/DMARC
envs/shared/           Route53 zone insolvia.ai, *.insolvia.ai ACM cert, insolvia-github-actions role, email
envs/staging/          web_hosting -> staging-app.insolvia.ai
envs/prod/             web_hosting -> app.insolvia.ai
```

State: `s3://insolvia-terraform-state`, key `insolvia/<env>/terraform.tfstate`.

## Usage

```bash
cd envs/<env>
terraform init
terraform plan     # offline validate: terraform init -backend=false && terraform validate
terraform apply    # gated off until insolvia.ai DNS is live
```

Apply order: `shared` → `staging` → `prod`. Never `destroy` `shared` before the
environments that depend on its zone/cert/role.
