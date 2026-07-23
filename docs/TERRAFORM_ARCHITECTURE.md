# Terraform architecture

## Two levels of state

Insolvia infra is split into a **shared** layer and per-**environment** layers,
each with its own isolated S3 state — never Terraform workspaces.

```
infra/
├── modules/
│   ├── web_hosting/          # reusable: S3 (private+OAC) + CloudFront + Route53 alias
│   ├── api_service/          # reusable: ECR + Docker Lambda + HTTP API + custom domain
│   │                         #   + waitlist DynamoDB + SSM config namespace + alarms
│   ├── auth/                 # reusable: Cognito user pool + hosted domain
│   │                         #   + web (SPA) and desktop (loopback) PKCE app clients
│   └── marketing_site/       # SSR marketing site: ECR + Lambda + HTTP API + S3 +
│                             # CloudFront (www + apex) + DynamoDB waitlist table
└── envs/
    ├── shared/               # account-wide, env-independent
    │                         #   • Route53 hosted zone  insolvia.ai
    │                         #   • ACM wildcard cert    *.insolvia.ai + apex SAN (us-east-1)
    │                         #   • IAM role             insolvia-github-actions (OIDC)
    ├── staging/              # web_hosting -> staging-app.insolvia.ai
    │                         # api_service -> staging-api.insolvia.ai
    │                         # auth        -> insolvia-users-staging
    ├── prod/                 # web_hosting -> app.insolvia.ai
    │                         # api_service -> api.insolvia.ai
    │                         # auth        -> insolvia-users-prod
    │                         # marketing_site -> www.insolvia.ai (+ apex 301)
    └── dev/                  # PER DEVELOPER MACHINE (see below) — waitlist
                              # table + auth pool, env suffix dev-<short-id>
```

| Env | State key (`s3://insolvia-terraform-state/…`) | Owns |
|---|---|---|
| shared | `insolvia/shared/terraform.tfstate` | zone, wildcard cert, deploy role |
| staging | `insolvia/staging/terraform.tfstate` | staging S3 + CloudFront + DNS record; staging API stack (ECR, Lambda, HTTP API, `insolvia-waitlist-staging`, alarms); staging auth (`insolvia-users-staging`) |
| prod | `insolvia/prod/terraform.tfstate` | prod S3 + CloudFront + DNS record; prod API stack (ECR, Lambda, HTTP API, `insolvia-waitlist-prod`, alarms); prod auth (`insolvia-users-prod`); the marketing stack (see below) |
| dev | `insolvia/dev/<account-id>/<machine-id>/terraform.tfstate` — one per developer machine | that machine's `insolvia-waitlist-dev-<short-id>` table and `insolvia-users-dev-<short-id>` pool |

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

## Auth (`infra/modules/auth/`)

One Cognito user pool per environment (issue #65): `insolvia-users-staging`
and `insolvia-users-prod`, fully separate — a staging token can never verify
against prod. Each owns, per env:

- **User pool** `insolvia-users-<env>` — email as username, **self-signup
  disabled** (attorneys are provisioned via `admin-create-user`), 12+ char
  password policy, optional TOTP MFA, ESSENTIALS plan (threat protection is a
  PLUS-plan upsell, deferred). `deletion_protection` is ACTIVE on prod only.
- **Hosted domain** — Cognito-provided prefix
  `insolvia-<env>.auth.us-east-1.amazoncognito.com`; a custom
  `auth.insolvia.ai` domain is deferred (vanity only, needs its own cert).
- **Two public PKCE app clients**, both authorization-code, no secret,
  refresh-token rotation enabled:
  - `insolvia-web-<env>` — the SPA; callbacks at
    `<origin>/auth/callback`, sign-out to the origin. Staging also registers
    `http://localhost:3000` (dev must run `flutter run --web-port 3000`);
    prod registers no dev origins.
  - `insolvia-desktop-<env>` — loopback redirect per RFC 8252: Cognito
    permits plain-HTTP callbacks only on `localhost`/`127.0.0.1`/`[::1]`
    and matches them **exactly** (no wildcard ports), so a fixed four-port
    set `http://127.0.0.1:{41539..41542}/callback` is registered and the
    desktop app must bind one of exactly those ports.

The API does **not** verify tokens yet — the env outputs expose
`auth_issuer_url` (and pool/client ids) as the seam; JWT verification wires
into `services/api` with the first authenticated endpoint.

## Per-machine development environment (`infra/envs/dev/`)

One instance of this env exists **per developer machine** — humbugg's dev-aws
pattern, adapted. A UUID generated once into `~/.config/insolvia/machine-id`
drives everything: its first 12 hex chars become the environment name
`dev-<short-id>` baked into every resource name, and the machine keeps its own
state key —

```
insolvia/dev/<account-id>/<machine-id>/terraform.tfstate
```

— injected at init time with `-backend-config="key=..."` (the backend block
declares no `key`), so two developers can never collide on names or state.

What it owns is deliberately only what local dev consumes today:

- **DynamoDB** `insolvia-waitlist-dev-<short-id>` — same PK/SK schema as
  `api_service`'s table so the API's adapter behaves identically, but PITR is
  **off** (throwaway data; `dev-aws-reset.sh` wipes it by design).
- **Auth** via the same `modules/auth` as staging/prod —
  `insolvia-users-dev-<short-id>`, hosted-domain prefix
  `insolvia-dev-<short-id>` (Cognito domain prefixes are globally unique
  across AWS; the short id is what makes a per-developer pool creatable at
  all), localhost-only web origin, deletion protection off. Outputs only —
  preps local auth work, nothing consumes it yet.

No ECR/Lambda/API Gateway/S3 (local dev runs the API via compose, not
Lambda), and no IAM — the developer's own credentials are the principal.

**CI never touches this env.** It is applied, reset, and destroyed only by
`scripts/dev-aws-{setup,reset,destroy}.sh` (see `scripts/README.md`) with the
developer's own profile; the PR gate only runs the same offline
`terraform validate -backend=false` it runs everywhere, and the deploy role
grants it nothing. Tags add `DeveloperMachineId`/`DeveloperPrincipal`/
`MachineName` to the standard set so an orphaned resource names its owner.

## Marketing site (`modules/marketing_site`, prod only)

The marketing site (`apps/insolvia_marketing`) is server-side rendered, so
`web_hosting` cannot host it. `marketing_site` is its own single-concern
module, instantiated **only in `envs/prod`** — the marketing site has no
staging environment (decision D2):

```
viewer ── CloudFront (www.insolvia.ai + insolvia.ai) ─┬─ /assets/*  → S3 (private, OAC)
                                                      └─ everything → HTTP API → SSR Lambda (Docker image from ECR)
```

- **Apex 301**: one distribution carries both aliases; a viewer-request
  CloudFront Function 301s `insolvia.ai/*` → `https://www.insolvia.ai/*`
  (path + query preserved). No second distribution.
- **X-Forwarded-Host** (app contract): the same function copies the viewer
  Host into `X-Forwarded-Host`, which the origin request policy forwards to
  the Lambda. The app's noindex logic and waitlist records depend on it —
  without it every production page ships `noindex`.
- **Waitlist**: the SSR action POSTs to the API's `/v1/waitlist`
  (`INSOLVIA_API_BASE_URL` on the Lambda); the table and its grant live with
  `api_service`, and the marketing Lambda holds no AWS data-plane access
  (docs/adr/0001).
- **Image lifecycle**: Terraform creates the Lambda from
  `<ecr>:{var.marketing_image_tag}` and then ignores `image_uri`; CI rolls
  images forward with `aws lambda update-function-code`. **First apply** needs
  the image to exist: `terraform apply -target=module.marketing_site.aws_ecr_repository.ssr`,
  push the image, then a full apply.

Names: `insolvia-marketing-prod` (ECR), `insolvia-marketing-ssr-prod`
(Lambda + HTTP API + role), `insolvia-marketing-assets-prod` (S3).

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
`main`; `prod` is `workflow_dispatch`-gated. `shared` is applied and the ACM
cert is `ISSUED`, so both env pipelines run for real. The ordering is not
ceremonial: every env looks the cert up with `statuses = ["ISSUED"]`, so in a
fresh account nothing downstream can even plan until `shared` has applied and
the cert has issued. The first `shared` apply must be preceded by a manual
`terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW` (#13) — that
import is done in this account.

## Destruction safety

Never `terraform destroy` `shared` casually — it holds the hosted zone and the
deploy role every other layer depends on. Tear down `prod`/`staging` first.

## Conventions
- Resources: `insolvia-<thing>-<env>` (e.g. `insolvia-web-staging`).
- Tags: `{ Project = "insolvia", Environment = <env>, ManagedBy = "terraform" }`.
- Sensitive vars `sensitive = true`; commit `terraform.tfvars.example`, never real `*.tfvars`.
- The infra directory is always `infra/`, never `terraform/`.
