# Terraform architecture

## Two levels of state

Insolvia infra is split into a **shared** layer and per-**environment** layers,
each with its own isolated S3 state — never Terraform workspaces.

```
infra/
├── modules/
│   ├── web_hosting/          # reusable: S3 (private+OAC) + CloudFront + Route53 alias
│   ├── api_service/          # reusable: Docker Lambda + alias + HTTP API + custom domain
│   │                         #   + waitlist DynamoDB + SSM config namespace + alarms
│   ├── auth/                 # reusable: Cognito user pool + hosted domain
│   │                         #   + one web (SPA) PKCE app client
│   ├── case_store/           # GLBA-scope case data: customer-managed KMS key +
│   │                         #   single-table DynamoDB + the API role's grant
│   ├── audit_trail/          # CloudTrail data events on the case store, into
│   │                         #   insolvia-audit-<env> under its own key
│   ├── mailer/               # transactional email: Lambda + S3 + DynamoDB + SES
│   └── marketing_site/       # SSR marketing site: Lambda + alias + HTTP API + S3 +
│                             # CloudFront (www + apex)
└── envs/
    ├── account-access/       # the account's HUMAN IAM principals — users,
    │                         #   groups, their attached policies. CI cannot
    │                         #   apply it (see below)
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
                              # table + case store + auth pool, env suffix
                              # dev-<short-id>; audit trail opt-in
```

| Env | State key (`s3://insolvia-terraform-state/…`) | Owns |
|---|---|---|
| ci-trust | `insolvia/ci-trust/terraform.tfstate` | GitHub OIDC provider + `insolvia-github-actions` deploy role + its policy — **human-applied only**, never by CI (see below) |
| account-access | `insolvia/account-access/terraform.tfstate` | The human IAM users, the groups they belong to, and the policies attached to them — **human-applied only**, and CI *cannot* apply it at all (see below) |
| shared | `insolvia/shared/terraform.tfstate` | zone, wildcard cert, SES identity + mail DNS, **the container repositories** (`insolvia-api`, `insolvia-marketing`, `insolvia-mailer` — one per service, shared by every env) |
| staging | `insolvia/staging/terraform.tfstate` | staging S3 + CloudFront + DNS record; staging API stack (Lambda, HTTP API, `insolvia-waitlist-staging`, alarms); staging auth (`insolvia-users-staging`) |
| prod | `insolvia/prod/terraform.tfstate` | prod S3 + CloudFront + DNS record; prod API stack (Lambda, HTTP API, `insolvia-waitlist-prod`, alarms); prod auth (`insolvia-users-prod`); the marketing stack (see below) |
| dev | `insolvia/dev/<account-id>/<machine-id>/terraform.tfstate` — one per developer machine | that machine's `insolvia-waitlist-dev-<short-id>` and `insolvia-cases-dev-<short-id>` tables and `insolvia-users-dev-<short-id>` pool; the audit trail only with `-var=enable_audit_trail=true` |

## Cross-layer references (data sources, not outputs)

Environments never read shared's state directly. They look resources up by
well-known name/domain:

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

## Human account access

`infra/envs/account-access/` — the account's **human** IAM principals: the
users, the groups they belong to, and the policies attached to them. Machines are elsewhere by construction: the
pipeline's identity is `ci-trust`, and every service role is created by the env
root that owns the service.

**CI cannot apply this root, and that is the control rather than an oversight.**
The deploy role holds no `iam:*User*` or `iam:*Group*` action anywhere in its
policy — `ServiceRoleManagement` is fenced to `role/insolvia-*` and nothing
else — so a CI apply fails on the first call. A pipeline that could create an
IAM user with `AdministratorAccess` could mint itself an admin, which is exactly
what `DenySelfPrivilegeEscalation` exists to prevent; keeping human principals
in a root CI cannot reach makes that property structural instead of a
permission someone forgot to add.

**It is separate from `ci-trust`, which is also human-applied,** for blast
radius and cadence. `ci-trust` holds the trust anchor every deploy
authenticates through — a botched plan there takes the pipeline offline. This
root changes when a person joins, leaves or moves group. Two states means a bad
edit to the user map can never propose a change to the deploy role.

Apply with `scripts/apply-account-access.sh` (credential dance → plan → confirm).

**What it deliberately does not model**, all for the same reason — the
credential would be written to `s3://insolvia-terraform-state` in plaintext:

| Not modelled | Why | Where it lives instead |
|---|---|---|
| `aws_iam_virtual_mfa_device` | Exposes `base_32_string_seed` — the TOTP shared secret — as an attribute | Console enrolment: [`iam-mfa-rotation.md`](../runbooks/iam-mfa-rotation.md) |
| `aws_iam_user_login_profile` | Generated password lands in state without a PGP key, and with one you have only moved the problem to key custody | Set by the person at first sign-in |
| `aws_iam_access_key` | Writes the secret access key to state | Created out of band; rotate by create → cut over → delete |

A self-service *"manage your own MFA"* policy is also absent, and the module
header says so explicitly so it is not re-added from a search result: every
user here is in `Admin`, so `AdministratorAccess` already allows every MFA
action (verified with `aws iam simulate-principal-policy`). It starts doing
work the day a non-admin user exists. MFA **enforcement**
(`aws:MultiFactorAuthPresent`) is a real open gap rather than a settled
decision — left out because a badly scoped version locks the account's only
human out, so it wants its own plan.

Two behaviours worth knowing before editing the user map:

- **Group membership is exclusive.** `aws_iam_user_group_membership` manages
  the full set, so a group added in the console is removed on the next apply.
- **Policy attachment is not.** There is no exclusive variant for users, so a
  policy attached by hand survives; catching that drift means reading
  `aws iam list-attached-user-policies`, not trusting the plan.

Offboarding is deliberately two applies: `aws_iam_user` carries
`prevent_destroy`, so removing someone means deleting that line and applying,
then removing them from the map and applying again. The account has one human
user and no other console path in — an accidental destroy locks everyone out
short of root recovery, and a real departure can afford a second plan.

## Backend API (`infra/modules/api_service/`)

One instance per environment (issue #63): `staging-api.insolvia.ai` and
`api.insolvia.ai`. Each owns, per env:

- **ECR** — *not* owned per env. One repository per service, `insolvia-api`,
  shared by staging and prod and created in `envs/shared`: prod deliberately
  deploys the exact image digest staging validated, which requires one place
  both envs can name it. Environment
  isolation lives in separate Lambdas, roles, tables, SSM namespaces and
  Cognito pools — never in separate image stores, and the image is
  environment-agnostic by construction (every service reads its environment at
  runtime). Retention is time-based with no catch-all rule, because ECR
  lifecycle rules only ever *expire* and never *protect*: a catch-all would
  evict the digest prod is running once staging churned past it.
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
  **no CloudFront** (an API
  gains nothing from an edge cache). A REGIONAL domain needs its cert in the
  API's own region — unlike CloudFront's unconditional us-east-1 — so the
  same shared wildcard-cert lookup serves both, only because everything is
  us-east-1.
- **DynamoDB** `insolvia-waitlist-<env>` — `PK`/`SK` string keys,
  PAY_PER_REQUEST, PITR. Lives here rather than with the marketing site per
  `docs/adr/0001`. The Lambda's role gets
  **PutItem only** (append-only by design), on its own env's table only.
- **SSM namespace** `/insolvia/<env>/api/<key>` (#70) — Terraform writes the
  values the service reads (`insolvia-env`, `waitlist-table-name`); the deploy
  workflow resolves the namespace into the Lambda environment. Future secrets
  join as SecureStrings with `ignore_changes = [value]`, so Terraform creates
  the slot but never owns the value.
- **Alarms** (#69) — Lambda errors and throttles, HTTP API `5xx`, p99 latency
  > 2 s sustained — all to an `insolvia-api-<env>-alarms` SNS topic.
  Subscribing an email is a manual step (confirmation click; no real addresses
  in this public repo).

### API bootstrap — image before apply

An Image Lambda cannot exist without an image, so a **fresh environment
deadlocks**: Terraform owns the repo the image must already be in. Once per
env:

```
terraform -chdir=infra/envs/shared apply          # creates insolvia-api
docker build --target lambda -t <repo-url>:<env> services/api && docker push <repo-url>:<env>
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
  `insolvia-<env>.auth.us-east-1.amazoncognito.com`, serving **managed login**
  (`managed_login_version = 2`), not the classic hosted UI. AWS calls the
  classic UI a "first-generation" service; managed login is what carries the
  branding editor, dark mode, localisation and the Terms/Privacy links at
  sign-up, and it is available from the ESSENTIALS tier this pool is already on.
  **The branding style is still not in Terraform**, but the provider is no
  longer why: `aws_cognito_managed_login_branding` shipped in v6.12.0 and the
  repo's floor is now well past it (`~> 6.37`; `infra/CLAUDE.md` owns why).
  What remains is that the authoritative copy of the style lives in the console
  and has to be exported before it can be codified — the module header records
  how.
- **Custom auth domain — supported by the module, not yet enabled, and blocked
  on the marketing launch.** `modules/auth` takes `custom_domain` +
  `certificate_arn` + `hosted_zone_id` and builds the domain alongside the
  prefix one (a pool may hold both, so the cutover costs no downtime — the app
  moves when its next build picks up `auth_domain`, and `prefix_domain` keeps
  serving). Every env passes null today.

  The blocker is Cognito's anti-hijacking check: it will not create a custom
  domain unless the **parent** domain resolves to an IP. Both
  `staging-auth.insolvia.ai` and `auth.insolvia.ai` sit one label under the
  apex, so both depend on `insolvia.ai` resolving — and it does not. The apex A
  record exists in Route53, but aliases prod's marketing distribution, which is
  parked (`site_enabled = false`). A disabled distribution serves no DNS.

  **Verify with `dig +short insolvia.ai A`, not by reading the Route53 record.**
  The record existing and the name resolving are different facts; assuming the
  first implied the second broke a staging apply.

  Names would be flat (`staging-auth`, not `auth.staging`) because the shared
  wildcard covers one label. Dev would keep the prefix domain regardless: a
  custom domain is per-pool and takes 15–20 minutes each way, so a per-machine
  one would add a quarter-hour to every `dev-aws-setup.sh` run.
- **One public PKCE app client**, authorization-code, no secret, refresh-token
  rotation enabled: `insolvia-web-<env>` — the SPA; callbacks at
  `<origin>/auth/callback`, sign-out to the origin. Staging also registers
  `http://localhost:3000`, so the dev server must serve on that exact port
  (`apps/insolvia_app/scripts/dev-up.sh` pins it); prod registers no dev
  origins. Cognito matches redirect URIs **exactly** — a different port is a
  different URI and Cognito rejects it, which is the whole reason the port is
  pinned rather than chosen per run. The web client is the only app client —
  a future native client registers the custom scheme
  `insolvia://auth/callback`, **not** an RFC 8252 loopback redirect (the
  header comment of `infra/modules/auth/main.tf` owns why).

The API **does** verify tokens: the env outputs publish `auth_issuer_url` and
the client id into `/insolvia/<env>/api/`, the deploy workflow derives them into
`AUTH_ISSUER_URL` / `AUTH_CLIENT_ID`, and `services/api` validates the issuer,
`token_use == "access"` and `client_id` against the pool's JWKS. Auth fails
**closed** — missing config is a 401 on every protected route, never a bypass.

## Case data store (`infra/modules/case_store/`)

The first persistent store of GLBA-scope data in the account — SSNs, full
financials — so the posture here is the one to copy, not to improvise on. The
logical model it holds is
[`case-data-model.md`](case-data-model.md); this section is how it is
protected. One instance per environment — `insolvia-cases-staging`,
`insolvia-cases-prod`, and `insolvia-cases-dev-<short-id>` on each developer
machine — each under its own key. Local is the same module, not an
approximation of it: there is no DynamoDB emulator here, so a KMS or IAM
mistake surfaces on a laptop instead of after a deploy.

**Encryption at rest.** A customer-managed KMS key per environment
(`alias/insolvia-cases-<env>`), rotation enabled, with the DynamoDB table's
`server_side_encryption` pointed at it. `enabled = true` alone would use the
AWS-owned DynamoDB key, which is not a key we control — the distinction is the
entire point. Staging and prod never share a key: the key is what makes prod
data unreadable to a staging mistake. In transit, every path is TLS — the API
reaches DynamoDB over the AWS SDK's HTTPS endpoint, and there is no other
caller.

**Who can read a case.** Exactly one principal: the API Lambda's execution
role, per [ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md). Two
grants, deliberately disjoint:

| Principal | Table | Key |
|---|---|---|
| API Lambda role | Item-level reads and writes, **no `Scan`**, no control plane | `Decrypt`/`GenerateDataKey`, fenced to `kms:ViaService = dynamodb` |
| CI deploy role | Control plane only — create, update, tag, PITR | Manage the key; **explicitly denied** `Decrypt`, `GenerateDataKey*` and `ReEncrypt*` except where SSM or CloudTrail is the calling service |

The API role's key grant is easy to mistake for redundancy, because DynamoDB
creates its own grant when the table is built. It is not: that grant covers
DynamoDB's key management, while the table-key `Decrypt` behind a read is
issued **on behalf of the calling principal**, against a per-caller cached key
that re-checks IAM when it expires. Delete the grant and reads keep working
until the cache turns over — the worst failure shape there is.

Table reads and writes additionally require `aws:SecureTransport`. The
DynamoDB endpoint accepts plain HTTP, and the SDK preferring HTTPS is a default
rather than a guarantee; the condition makes TLS in transit a control instead
of an assumption.

The absence of `dynamodb:Scan` is load-bearing rather than tidy: every read in
the model is keyed, and a Scan on this table is a full read of every debtor's
financials. Adding one should require a diff that says so.

**On the key policy, and "no human read paths in prod".** The key policy grants
the account root `kms:*` — AWS's default, and it means "IAM identity policies
decide", not "everyone can decrypt". The stricter alternative, naming only the
API role and omitting root, is what would make "no human read path" a property
of the key itself; it is rejected because a key policy naming no principal able
to change it is unrecoverable. The property is delivered instead by no human
principal holding DynamoDB data-plane or KMS decrypt permissions, and by the
deploy role's explicit deny in `infra/envs/ci-trust`. That is a weaker
guarantee honestly stated, rather than a stronger one that risks locking the
account out of its own data.

**Backups.** Point-in-time recovery on both environments. Note what PITR does
*not* cover: a restore is encrypted under the same key, so scheduling the key's
deletion destroys the backups too. Prod therefore carries the maximum 30-day
key deletion window and DynamoDB deletion protection; staging carries neither,
holding only synthetic data.

The key itself has no `prevent_destroy`, which looks like an omission and is
not. It would bind staging too — `prevent_destroy` takes a literal, not a
variable — and staging's disposability is the point. Prod's key is defended
twice over instead: the table depends on the key, so a `terraform destroy`
reaches the table first and aborts on its deletion protection, and even a
scheduled key deletion is cancellable for 30 days.

**The access log.** A second table, `insolvia-case-access-log-<env>`, keyed
`PK = CASE#<id>` / `SK = <recorded_at>#<event_id>`, under the same key. It
answers the question the CloudTrail trail structurally cannot: *which
signed-in user* read this case. ADR 0001 means AWS only ever sees the API
role, so the end user's identity exists solely inside the request — this is
where the API writes it down. **Reads are logged, not just writes**; the
provenance fields on the case record already answer "who changed this", and
who *saw* it is the question with no other source.

**Nothing in it expires, and TTL cannot be turned on from the pipeline.**
DynamoDB authorises `UpdateTimeToLive` against the caller, and on a
CMK-encrypted table that needs `kms:Decrypt` on the key — which `ci-trust`
explicitly denies the deploy role on `alias/insolvia-cases-*`. There is no
condition key separating "enable TTL" from "read a row"; both are
`kms:ViaService = dynamodb`. So the choice is retention or a deploy role that
can read case data, and the deny wins. That also matches the posture
`DenyAuditLogErasure` takes for the trail bucket: a retention rule the pipeline
can set is a delete button the pipeline can press, and TTL deletes silently.
Retention here is a compliance decision for the regulatory register and a human
apply, not a Terraform default.

The API's grant on it is **`PutItem` and nothing else** — no `GetItem`, no
`Query`, no update, no delete. The audited service cannot read, amend or
remove its own audit trail. The consequence is real and intended: no endpoint
can serve an access history today, and adding one means granting `Query` in a
diff that says so rather than inheriting the capability by accident.
Retention is by TTL, and the number is a compliance decision the
[regulatory register](../business/regulatory-source-register.html) owns rather
than an engineering one.

A by-principal index — "what did this account touch" — is deliberately not
here yet. It is the breach-response question rather than the client question,
and a GSI can be added online later without a migration.

**Audit logging** lives next door, in `infra/modules/audit_trail/` — see below.

**Wiring.** The module takes `api_role_name` — a name, not an ARN — looks the
role up with a data source, and attaches the table grant from its own side.
That is the mailer's pattern, and it is what avoids a cycle, since
`api_service` must not know this module exists. For the same reason the table
name reaches the API through an **env-level** `aws_ssm_parameter` rather than
through `api_service`'s own parameter map, landing as `CASE_TABLE_NAME` via the
deploy workflow's existing `get-parameters-by-path` step. The key ARN is not
published: DynamoDB decrypts under its own grant, so the API never names it.

## Case-data audit trail (`infra/modules/audit_trail/`)

CloudTrail data events for the stores holding GLBA-scope data, into an
`insolvia-audit-<env>` bucket under its own key. One instance per deployed
environment; on a developer machine it is opt-in (below).

**What it can and cannot prove.** Because
[ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md) makes the API
Lambda's role the only application principal, every data event names
`insolvia-api-<env>-role` and never the signed-in user behind it. That makes
this evidence about **administrative** access — the "no human read paths in
prod" claim — and about the shape and volume of application access. *Which
user read this SSN* is a different question that only an application-level log
the API writes can answer. Do not let this trail stand in for that one.

**Its key is not the case key**, and that is forced rather than chosen:
`ci-trust`'s `DenyCaseDataDecryption` denies the deploy role
`kms:GenerateDataKey` on anything aliased `alias/insolvia-cases-*`, so a trail
pointed at the case key fails at `CreateTrail`. The separation is worth having
anyway — the deploy role holds no `kms:Decrypt` for audit keys in any
statement, so the pipeline can create this trail and write to it and can never
read back what it recorded.

**Tamper resistance is a floor, not a guarantee.** `DenyAuditLogErasure` denies
the deploy role `DeleteObject` and `DeleteObjectVersion`, and bucket versioning
is what makes the second half meaningful — without it an overwrite is a silent
deletion that calls no Delete API. The role still holds `s3:*` otherwise, so a
lifecycle rule or a bucket deletion remains open to it. Real immutability is S3
Object Lock, which is deliberately not here yet.

**Scope and cost.** Data events bill per event, so the selector names the case
table specifically rather than "all DynamoDB tables"; the case document bucket
joins `data_resource_arns` when 8.6 lands. Management events are excluded —
they would double the volume to say what the Terraform diff already says.
Retention is 90 days on staging and a year on prod.

**Locally** it is off by default: a per-developer trail bills to record a
developer reading their own synthetic cases. It can still be stood up on a
laptop, which is the part that matters —

```
terraform -chdir=infra/envs/dev apply -var=enable_audit_trail=true ...
```

— so a change to this module is exercised before staging meets it, rather
than after. Turn it back off and re-apply to remove it.

## Per-machine development environment (`infra/envs/dev/`)

One instance of this env exists **per developer machine**. A UUID generated
once into `~/.config/insolvia/machine-id`
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
- **Case store** via the same `modules/case_store` as staging/prod —
  `insolvia-cases-dev-<short-id>` and its access-log table under this
  machine's own customer-managed key, so the tenancy indexes and the encrypted read path behave here exactly as
  they do deployed. PITR and deletion protection are **off** and the key
  deletion window is the minimum 7 days, so a teardown does not leave a
  month-long pending key behind. `api_role_name` is null — there is no Lambda,
  so no grant is created and the developer's own credentials are the
  principal.
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

## Marketing site (`modules/marketing_site`, staging + prod)

The marketing site (`apps/insolvia_marketing`) is server-side rendered, so
`web_hosting` cannot host it. `marketing_site` is its own single-concern
module, instantiated in **both** `envs/staging` and `envs/prod`:

```
viewer ── CloudFront (www.insolvia.ai + insolvia.ai) ─┬─ /assets/*  → S3 (private, OAC)
                                                      └─ everything → HTTP API → SSR Lambda (Docker image from ECR)
```

- **Apex 301**: one distribution carries both aliases; a viewer-request
  CloudFront Function 301s `insolvia.ai/*` → `https://www.insolvia.ai/*`
  (path + query preserved). No second distribution.
- **Only prod owns the apex.** A zone has exactly one apex, so staging passes
  `apex_domain = null` and the module drops the apex alias, the apex A/AAAA
  records, and the 301 branch of the viewer-request function. Staging serves
  `staging-www.insolvia.ai` and nothing else. Passing the apex in both
  environments would make the second apply fail on the CloudFront alias
  conflict — the module's variable documentation says so at the point of use.
- **X-Forwarded-Host** (app contract): the same function copies the viewer
  Host into `X-Forwarded-Host`, which the origin request policy forwards to
  the Lambda. The app's noindex logic and waitlist records depend on it —
  without it every production page ships `noindex`.
- **Waitlist**: the SSR action POSTs to the API's `/v1/waitlist`
  (`INSOLVIA_API_BASE_URL` on the Lambda); the table and its grant live with
  `api_service`, and the marketing Lambda holds no AWS data-plane access
  (docs/adr/0001).
- **Image lifecycle**: Terraform creates the Lambda from
  `<ecr>:{var.marketing_image_tag}` (this env's moving marker tag) and then
  ignores `image_uri`; CI rolls images forward with
  `aws lambda update-function-code`. **First apply** needs the image to exist:
  apply `infra/envs/shared` (which creates the repository), push the image,
  then a full apply.

Names: `insolvia-marketing` (ECR — shared across envs, no suffix),
`insolvia-marketing-ssr-prod` (Lambda + HTTP API + role),
`insolvia-marketing-assets-prod` (S3).

## Providers

Every env declares the default `aws` provider (region `us-east-1`) and an aliased
`aws.us_east_1` used for ACM/CloudFront (identical here, but kept explicit to
match convention and stay portable if the default region ever changes).

Each root commits its `.terraform.lock.hcl`, with hashes for both platforms
that run Terraform here (linux_amd64 in CI, darwin_arm64 on developer
machines), so every checkout applies the same provider build against the same
state — the version constraint alone only sets a floor, and two machines
resolving it at different times used to run different provider versions
against `envs/dev`. The upgrade procedure lives in
[`infra/CLAUDE.md`](../../infra/CLAUDE.md).

## Region

**Everything is `us-east-1`.** CloudFront requires its ACM certificate in
`us-east-1`, so we keep the whole footprint there for simplicity.

## Deployment order

```
ci-trust  →  shared  →  staging  →  prod
```
`ci-trust` must exist first: it holds the OIDC provider + deploy role that CI
*assumes* to apply everything else. It is **human-applied only** — no CI
workflow touches it — because the deploy role can't grant itself permissions
(`DenySelfPrivilegeEscalation`). Then `shared` (zone + cert + SES). CI applies
`staging` on merge to `main`; `prod` behind the release's `insolvia-production`
approval (or an out-of-band `infra-prod.yml` dispatch). The ordering
is not ceremonial, for two independent reasons: every env looks the cert up
with `statuses = ["ISSUED"]`, **and** every env looks its container
repositories up with `data "aws_ecr_repository"`. Both hard-fail until
`shared` has applied, so in a fresh account nothing downstream can even plan
before then — and `shared` itself can't be CI-applied until the `ci-trust`
role exists.

CI orders `shared` against everything downstream: `shared-infra-deploy.yml`
has no push trigger of its own — release.yml calls it (path-filtered) as the
first leg of its staging stage, ahead of the staging apply, so a merge that
adds a shared resource plus its consumer can never have the consumer's `data`
lookup race the apply that creates it. (It used to run concurrently from its
own trigger, and a staging run that lost the race failed on the lookup.) The
first `shared` apply must be preceded by a manual
`terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW` (#13) — that
import is done in this account.

Changing the deploy role's permissions is a `ci-trust` apply, and it must be
run by a human admin (`scripts/apply-ci-trust.sh`) — CI cannot, by the
self-deny above. This is why adding an IAM permission the pipeline needs is
never a "just merge it" change.

`account-access` is **outside this ordering entirely** — nothing depends on it
and it depends on nothing, because IAM users are principals a human uses, not
inputs any deploy reads. Apply it whenever the people change
(`scripts/apply-account-access.sh`); it is never a step in standing up an
environment.

## Destruction safety

Never `terraform destroy` `shared` casually — it holds the hosted zone and the
deploy role every other layer depends on. Tear down `prod`/`staging` first.

## Conventions
- Resources: `insolvia-<thing>-<env>` (e.g. `insolvia-web-staging`).
- Tags: `{ Project = "insolvia", Environment = <env>, ManagedBy = "terraform" }`.
- Sensitive vars `sensitive = true`; commit `terraform.tfvars.example`, never real `*.tfvars`.
- The infra directory is always `infra/`, never `terraform/`.
