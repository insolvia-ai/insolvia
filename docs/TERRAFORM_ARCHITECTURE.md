# Terraform architecture

## Two levels of state

Insolvia infra is split into a **shared** layer and per-**environment** layers,
each with its own isolated S3 state — never Terraform workspaces.

```
infra/
├── modules/
│   └── web_hosting/          # reusable: S3 (private+OAC) + CloudFront + Route53 alias
└── envs/
    ├── shared/               # account-wide, env-independent
    │                         #   • Route53 hosted zone  insolvia.ai
    │                         #   • ACM wildcard cert    *.insolvia.ai (us-east-1)
    │                         #   • IAM role             insolvia-github-actions (OIDC)
    ├── staging/              # web_hosting -> staging-app.insolvia.ai
    └── prod/                 # web_hosting -> app.insolvia.ai
```

| Env | State key (`s3://insolvia-terraform-state/…`) | Owns |
|---|---|---|
| shared | `insolvia/shared/terraform.tfstate` | zone, wildcard cert, deploy role |
| staging | `insolvia/staging/terraform.tfstate` | staging S3 + CloudFront + DNS record |
| prod | `insolvia/prod/terraform.tfstate` | prod S3 + CloudFront + DNS record |

## Cross-layer references (data sources, not outputs)

Environments never read shared's state directly. They look resources up by
well-known name/domain, exactly like `andreas-services`:

```hcl
data "aws_route53_zone" "main" {
  name = "insolvia.ai"
}

data "aws_acm_certificate" "wildcard" {
  domain      = "*.insolvia.ai"
  provider    = aws.us_east_1
  statuses    = ["ISSUED"]
  most_recent = true
}
```

## Providers

Every env declares the default `aws` provider (region `us-east-1`) and an aliased
`aws.us_east_1` used for ACM/CloudFront (identical here, but kept explicit to
match convention and stay portable if the default region ever changes).

## Region

**Everything is `us-east-1`.** CloudFront requires its ACM certificate in
`us-east-1`, so we keep the whole footprint there for simplicity.

## Deployment order

```
shared  →  staging  →  prod
```
`shared` must exist first (zone + cert + role). CI applies `staging` on merge to
`main`; `prod` is `workflow_dispatch`-gated. **All applies are gated off until
`insolvia.ai` DNS is live** (`DEPLOY_ENABLED` repo variable).

## Destruction safety

Never `terraform destroy` `shared` casually — it holds the hosted zone and the
deploy role every other layer depends on. Tear down `prod`/`staging` first.

## Conventions
- Resources: `insolvia-<thing>-<env>` (e.g. `insolvia-web-staging`).
- Tags: `{ Project = "insolvia", Environment = <env>, ManagedBy = "terraform" }`.
- Sensitive vars `sensitive = true`; commit `terraform.tfvars.example`, never real `*.tfvars`.
- The infra directory is always `infra/`, never `terraform/`.
