# Terraform architecture

## Two levels of state

Insolvia infra is split into a **shared** layer and per-**environment** layers,
each with its own isolated S3 state — never Terraform workspaces.

```
infra/
├── modules/
│   ├── web_hosting/          # reusable: S3 (private+OAC) + CloudFront + Route53 alias
│   └── api_service/          # reusable: ECR + Docker Lambda + HTTP API + custom domain
│                             #   + waitlist DynamoDB + SSM config namespace + alarms
└── envs/
    ├── shared/               # account-wide, env-independent
    │                         #   • Route53 hosted zone  insolvia.ai
    │                         #   • ACM wildcard cert    *.insolvia.ai (us-east-1)
    │                         #   • IAM role             insolvia-github-actions (OIDC)
    ├── staging/              # web_hosting -> staging-app.insolvia.ai
    │                         # api_service -> staging-api.insolvia.ai
    └── prod/                 # web_hosting -> app.insolvia.ai
                              # api_service -> api.insolvia.ai
```

| Env | State key (`s3://insolvia-terraform-state/…`) | Owns |
|---|---|---|
| shared | `insolvia/shared/terraform.tfstate` | zone, wildcard cert, deploy role |
| staging | `insolvia/staging/terraform.tfstate` | staging S3 + CloudFront + DNS record; staging API stack (ECR, Lambda, HTTP API, `insolvia-waitlist-staging`, alarms) |
| prod | `insolvia/prod/terraform.tfstate` | prod S3 + CloudFront + DNS record; prod API stack (ECR, Lambda, HTTP API, `insolvia-waitlist-prod`, alarms) |

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

## Backend API (`infra/modules/api_service/`)

One instance per environment (issue #63): `staging-api.insolvia.ai` and
`api.insolvia.ai`. Each owns, per env:

- **ECR** `insolvia-api-<env>` — scan on push, keep-last-10 lifecycle. Separate
  repos per env, so a prod deploy can never reference a staging image.
- **Lambda (Image)** `insolvia-api-<env>` — 30 s / 512 MB, Flask+Mangum from
  `services/api/`. `lifecycle { ignore_changes = [image_uri, environment] }`:
  the **deploy workflow owns both** — it pushes an image and injects the
  environment it resolves from SSM (below), so Terraform's copies are only the
  first-apply seed.
- **HTTP API** — `$default` route to the Lambda, payload format 2.0 (what
  Mangum consumes); stage throttling 20 rps / burst 40 as the unauthenticated
  waitlist endpoint's abuse control; execute-api endpoint disabled so the
  custom domain is the only front door.
- **Custom domain** — an API Gateway REGIONAL domain + Route53 alias,
  **no CloudFront** (deviating from #62's title; the mailer precedent — an API
  gains nothing from an edge cache). A REGIONAL domain needs its cert in the
  API's own region — unlike CloudFront's unconditional us-east-1 — so the
  same shared wildcard-cert lookup serves both, only because everything is
  us-east-1.
- **DynamoDB** `insolvia-waitlist-<env>` — `PK`/`SK` string keys,
  PAY_PER_REQUEST, PITR. Moved here from the marketing site per
  `docs/adr/0001`; deliberately not named `insolvia-marketing-waitlist-*`,
  which coexists until the marketing module drops it. The Lambda's role gets
  **PutItem only** (append-only by design), on its own env's table only.
- **SSM namespace** `/insolvia/<env>/api/<key>` (#70) — Terraform writes the
  values the service reads (`insolvia-env`, `waitlist-table-name`); the deploy
  workflow resolves the namespace into the Lambda environment. Future secrets
  join as SecureStrings with `ignore_changes = [value]`, like
  `/insolvia/shared/inbound-forward-to`.
- **Alarms** (#69) — Lambda errors and throttles, HTTP API `5xx`, p99 latency
  > 2 s sustained — all to an `insolvia-api-<env>-alarms` SNS topic.
  Subscribing an email is a manual step (confirmation click; no real addresses
  in this public repo).

### API bootstrap — image before apply

An Image Lambda cannot exist without an image, so a **fresh environment
deadlocks**: Terraform owns the repo the image must already be in. Once per
env:

```
terraform apply -target=module.api_service.aws_ecr_repository.api
docker build --target lambda -t <repo-url>:latest services/api && docker push <repo-url>:latest
terraform apply
```

Steady state is workflow-driven: push image → `aws lambda
update-function-code` → resolve `/insolvia/<env>/api/*` →
`update-function-configuration`. Terraform never notices.

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
`main`; `prod` is `workflow_dispatch`-gated. **All applies are gated off**
(`DEPLOY_ENABLED` repo variable, currently `false`). DNS is live; the gate now
waits on `shared` being applied (#15) and the ACM cert reaching `ISSUED` (#16).
The first `shared` apply must be preceded by a manual
`terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW` (#13).

## Destruction safety

Never `terraform destroy` `shared` casually — it holds the hosted zone and the
deploy role every other layer depends on. Tear down `prod`/`staging` first.

## Conventions
- Resources: `insolvia-<thing>-<env>` (e.g. `insolvia-web-staging`).
- Tags: `{ Project = "insolvia", Environment = <env>, ManagedBy = "terraform" }`.
- Sensitive vars `sensitive = true`; commit `terraform.tfvars.example`, never real `*.tfvars`.
- The infra directory is always `infra/`, never `terraform/`.
