# Terraform architecture

## Two levels of state

Insolvia infra is split into a **shared** layer and per-**environment** layers,
each with its own isolated S3 state — never Terraform workspaces. **The two
layers live in two repositories.** This repo holds the environment roots
(`staging`, `prod`, `dev`) and the modules they call. The shared layer —
`shared`, `ci-trust`, `account-access` — and the mailer's modules live in
[`insolvia-ai/insolvia-services`](https://github.com/insolvia-ai/insolvia-services),
whose own [`docs/reference/terraform.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/reference/terraform.md) owns
them ([ADR 0025](../adr/0025-shared-infra-and-mailer-move-to-insolvia-services.md)).
This document describes them only where an environment here depends on them.

```
infra/
├── modules/
│   ├── web_hosting/          # reusable: S3 (private+OAC) + CloudFront + Route53 alias
│   ├── api_service/          # reusable: Docker Lambda + alias + HTTP API + custom domain
│   │                         #   + SSM config namespace + alarms
│   ├── auth/                 # the CONSUMER half of auth (ADR 0026): the contract
│   │                         #   reader below + its roles' AdminCreateUser grants
│   ├── auth_contract/        # reads insolvia-services' SSM contract
│   │                         #   /insolvia/<env>/auth/* (the pool's identifiers)
│   ├── case_store/           # GLBA-scope case data's customer-managed KMS key +
│   │                         #   the roles' tax-id envelope grants (rows: Aurora)
│   ├── network/              # ADR 0028: VPC, two AZs of private subnets, a fck-nat
│   │                         #   egress instance, an S3 gateway endpoint,
│   │                         #   the backend Lambdas' security group (staging, prod)
│   ├── aurora/               # ADR 0028: Aurora Serverless v2 PostgreSQL 17 under its
│   │                         #   own KMS key + the /insolvia/<env>/db/* SSM contract
│   │                         #   inputs (staging, prod)
│   ├── vpc_lambda_access/    # ADR 0028: the two grants every backend Lambda's role
│   │                         #   holds — its VPC network interfaces, and
│   │                         #   rds-db:connect as its own Postgres role
│   ├── migrator/             # ADR 0028: the one-shot in-VPC migrator Lambda CI
│   │                         #   invokes before every alias shift (and its staging
│   │                         #   seed reach); the only reader of the master secret
│   ├── audit_trail/          # CloudTrail management events (KMS key use), into
│   │                         #   insolvia-<env>-audit under its own key
│   ├── filing_credentials/   # the CM/ECF credential vault (ADR 0024): a dedicated
│   │                         #   KMS key only the filing worker's role may decrypt,
│   │                         #   that role, the API's seal-only grant
│   ├── filing_queue/         # the filing worker's own queue (ADR 0024 PR 6): SQS
│   │                         #   + DLQ, the API's send-only and the worker's
│   │                         #   consume-only grants
│   ├── filing_worker/        # the filing worker (ADR 0024 PR 7): image Lambda +
│   │                         #   event source mapping + alarms (absent in dev),
│   │                         #   the kill switch, and every grant the worker
│   │                         #   role holds on case data, pinned whole
│   ├── job_pipeline/         # async jobs (ADR 0018): SQS queue + DLQ + the
│   │                         #   image-packaged worker Lambda + alarms; the
│   │                         #   worker half is optional (absent in dev)
│   └── marketing_site/       # SSR marketing site: Lambda + alias + HTTP API + S3 +
│                             # CloudFront (www + apex)
└── envs/                     # (shared, ci-trust, account-access: insolvia-services)
    ├── staging/              # web_hosting -> staging-app.insolvia.ai
    │                         # api_service -> staging-api.insolvia.ai
    │                         # auth        -> insolvia-staging-users
    ├── prod/                 # web_hosting -> app.insolvia.ai
    │                         # api_service -> api.insolvia.ai
    │                         # auth        -> insolvia-prod-users
    │                         # marketing_site -> www.insolvia.ai (+ apex 301)
    └── dev/                  # PER DEVELOPER MACHINE (see below) — case and
                              # vault keys, queues, document bucket, env suffix
                              # dev-<short-id>; audit trail opt-in
```

| Env | State key (`s3://insolvia-shared-terraform-state-us-east-1/…`) | Owns |
|---|---|---|
| ci-trust *(insolvia-services)* | `insolvia/ci-trust/terraform.tfstate` | GitHub OIDC provider + `insolvia-shared-deploy-role` deploy role + its policy, and the staging seed role — **human-applied only**, never by CI. The deploy role trusts both repositories; the seed role trusts this one only |
| account-access *(insolvia-services)* | `insolvia/account-access/terraform.tfstate` | The human IAM users, their groups and attached policies — **human-applied only**, and CI *cannot* apply it at all |
| shared *(insolvia-services)* | `insolvia/shared/terraform.tfstate` | zone, wildcard cert, SES identity + mail DNS, **the container repositories** (`insolvia-shared-api`, `insolvia-shared-marketing`, `insolvia-shared-jobs`, … — one per image, including every service of this repo's, shared by every env), and **the seed-fixture bucket** (`insolvia-shared-dev-fixtures-us-east-1` — the bytes behind `seeds/fixtures/`, read by every dev stack and by staging's seed step; CI applies it and never writes it) |
| staging | `insolvia/staging/terraform.tfstate` | staging S3 + CloudFront + DNS record; staging API stack (Lambda, HTTP API, alarms); staging auth (`insolvia-staging-users`) |
| prod | `insolvia/prod/terraform.tfstate` | prod S3 + CloudFront + DNS record; prod API stack (Lambda, HTTP API, alarms); prod auth (`insolvia-prod-users`); the marketing stack (see below) |
| dev | `insolvia/dev/<account-id>/<machine-id>/terraform.tfstate` — one per developer machine | that machine's case and vault keys, queues and document bucket (it reads the shared `insolvia-dev-users` pool); the audit trail only with `-var=enable_audit_trail=true` |

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

The account's **human** IAM principals live in insolvia-services'
[`infra/envs/account-access/`](https://github.com/insolvia-ai/insolvia-services/blob/main/infra/envs/account-access/main.tf), applied by
a human with that repo's `scripts/apply-account-access.sh`. Its
[`docs/reference/terraform.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/reference/terraform.md) owns why CI
cannot apply it, what it deliberately does not model, and the MFA procedure
([`iam-mfa-rotation.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/iam-mfa-rotation.md)). Nothing in this
repo depends on it.

## The backend on .NET and Aurora (ADR 0028, plan 18.22)

Since the switch every backend Lambda runs a `backend/` image and keeps its
data in the environment's Aurora cluster. What that means in Terraform, host
by host (the per-module sections below keep their history):

| Host (Lambda) | Image repository | Postgres role | SSM namespace (+ `/db/*`) | Grants beyond the database |
|---|---|---|---|---|
| `insolvia-<env>-api` | `insolvia-shared-api-dotnet` (`lambda`) | `api` | `/insolvia/<env>/api` | S3 documents, KMS tax-id seal+open and vault seal (by encryption context), SQS send on both queues, Cognito AdminCreateUser, the mailer's execute-api (insolvia-services) |
| `insolvia-<env>-jobs-worker` | `insolvia-shared-worker-dotnet` (`worker-lambda`) | `worker` | `/insolvia/<env>/worker` | SQS consume (via the mapping), S3 documents read + packets write, KMS tax-id open |
| `insolvia-<env>-admin-api` | `insolvia-shared-admin-api-dotnet` (`admin-lambda`) | `admin` | `/insolvia/<env>/admin-api` | Cognito AdminCreateUser |
| `insolvia-<env>-mcp` | `insolvia-shared-mcp-dotnet` (`mcp-lambda`) | `mcp` | `/insolvia/<env>/mcp` | none — no KMS at all |
| `insolvia-<env>-filing-worker` | `insolvia-shared-filing-dotnet` (`filing-lambda`) | `filing` | none: Terraform owns its environment (names only) | SQS consume, S3 packets read + filings write, KMS tax-id open and vault open, the kill switch |
| `insolvia-<env>-migrator` | `insolvia-shared-migrator-dotnet` (`migrator`) | the RDS master, then `SET ROLE migrator` | `/insolvia/<env>/migrator` | the master secret's GetSecretValue; on staging also Cognito (Get/Create/SetPassword), the fixture bucket read, documents write, the case key through S3 and for tax-id sealing |

- **The network.** Each runs in `module.network`'s private subnets under its
  Lambda security group, the one `module.aurora` admits 5432 from.
  `modules/vpc_lambda_access` adds the ENI permissions as an INLINE policy —
  ci-trust lets the deploy role attach only AWSLambdaBasicExecutionRole — and
  the per-role `rds-db:connect` on
  `dbuser:<cluster resource id>/<role>`. The role name is one literal per
  module, published as `insolvia-db-username` beside it, so the grant and
  `INSOLVIA_DB_USERNAME` cannot disagree. The migrator creates those roles
  `WITH rds_iam` on every run.
- **The database contract** is `/insolvia/<env>/db/{endpoint,port,database-name,cluster-resource-id,master-secret-arn}`
  (env root). `.github/scripts/lambda-environment.sh` maps it to the
  `INSOLVIA_DB_*` names `backend/src/Insolvia.Data` reads — the one place the
  infra's kebab naming and the backend's variable names meet — and passes the
  master secret's ARN to the migrator alone. `AWS_REGION` is never published:
  Lambda reserves it and sets it itself.
- **The key aliases are explicit** (`insolvia-kms-case-key-alias`,
  `insolvia-kms-filing-key-alias`); the .NET hosts no longer derive them from
  a table name. A generated `insolvia-cursor-key` (SecureString) binds the
  API's and MCP's keyset cursors.
- **There is no DynamoDB.** The cases, case-access-log, firms,
  filing-credentials, waitlist and admin-audit tables were deleted in plan
  18.23, in two applies because DynamoDB refuses to delete a protected table
  (deletion protection and every grant off first, then the resources), with
  `modules/firm_store` and the VPC's DynamoDB gateway endpoint. What a role
  keeps on the case and vault keys is the tax-id and vault envelopes, and the
  S3 fence.
- **The migrator runs before every alias shift** — `release.yml`'s first leg
  after infra in both stages ([`architecture.md`](architecture.md) § CI/CD).

## Backend API (`infra/modules/api_service/`)

One instance per environment (issue #63): `staging-api.insolvia.ai` and
`api.insolvia.ai`. Each owns, per env:

- **ECR** — *not* owned per env. One repository per service,
  `insolvia-shared-api-dotnet` since the switch (`insolvia-shared-api` held the
  Python image),
  shared by staging and prod and created in insolvia-services'
  `infra/envs/shared`: prod deliberately
  deploys the exact image digest staging validated, which requires one place
  both envs can name it. Environment
  isolation lives in separate Lambdas, roles, tables, SSM namespaces and
  Cognito pools — never in separate image stores, and the image is
  environment-agnostic by construction (every service reads its environment at
  runtime). Retention is time-based with no catch-all rule, because ECR
  lifecycle rules only ever *expire* and never *protect*: a catch-all would
  evict the digest prod is running once staging churned past it.
- **Lambda (Image)** `insolvia-<env>-api` — 30 s / 1024 MB, ASP.NET Core
  from `backend/src/Insolvia.Api` (Flask+Mangum from the since-deleted
  `services/api/` before the switch), in the VPC. `lifecycle { ignore_changes = [image_uri, environment] }`:
  the **deploy workflow owns both** — it pushes an image and injects the
  environment it resolves from SSM (below), so Terraform's copies are only the
  first-apply seed.
- **HTTP API** — `$default` route to the Lambda, payload format 2.0 (what
  `AddAWSLambdaHosting(LambdaEventSource.HttpApi)` consumes); stage throttling 20 rps / burst 40 as the unauthenticated
  waitlist endpoint's abuse control; execute-api endpoint disabled so the
  custom domain is the only front door.
- **Custom domain** — an API Gateway REGIONAL domain + Route53 alias,
  **no CloudFront** (an API
  gains nothing from an edge cache). A REGIONAL domain needs its cert in the
  API's own region — unlike CloudFront's unconditional us-east-1 — so the
  same shared wildcard-cert lookup serves both, only because everything is
  us-east-1.
- **No data store.** The waitlist is the `waitlist_entries` Postgres table,
  INSERT-only for the `api` role; the DynamoDB `insolvia-<env>-waitlist` this
  module held went in 18.23.
- **SSM namespace** `/insolvia/<env>/api/<key>` (#70) — Terraform writes the
  values the service reads (`insolvia-env`, `insolvia-db-username`, the cursor
  key, and the env root's auth, queue, bucket and key-alias values); the deploy
  workflow resolves the namespace into the Lambda environment. Future secrets
  join as SecureStrings with `ignore_changes = [value]`, so Terraform creates
  the slot but never owns the value.
- **Alarms** (#69) — Lambda errors and throttles, HTTP API `5xx`, p99 latency
  > 2 s sustained — all to an `insolvia-<env>-api-alarms` SNS topic.
  Subscribing an email is a manual step (confirmation click; no real addresses
  in this public repo).

### API bootstrap — image before apply

An Image Lambda cannot exist without an image, so a **fresh environment
deadlocks**: Terraform owns the repo the image must already be in. Once per
env:

```
# in insolvia-services: release its infra/envs/shared (creates insolvia-shared-api-dotnet)
scripts/bootstrap-ecr-images.sh <env> api      # docker build --target lambda backend, push :<env>
terraform apply
```

Steady state is workflow-driven: push image → `aws lambda
update-function-code` → resolve `/insolvia/<env>/api/*` + `/db/*`
(`.github/scripts/lambda-environment.sh`) → `update-function-configuration`.
Terraform never notices. **At the switch only the migrator needed this**: the
other Lambdas already existed, and their ignored `image_uri` lets the deploy
legs move them onto the `-dotnet` repositories themselves.

## Async job pipeline (`infra/modules/job_pipeline/`)

The orchestration half of
[ADR 0018](../adr/0018-sqs-queue-and-worker-lambda-over-step-functions.md):
per environment, one SQS queue (`insolvia-<env>-jobs`) the API enqueues
accepted jobs onto, a DLQ, and one image-packaged worker Lambda
(`insolvia-<env>-jobs-worker`) consuming it at batch size 1. The job *record*
lives in Aurora — this module owns no store.

- **The worker image** is `insolvia-shared-worker-dotnet` — backend/Dockerfile's
  `worker-lambda` target (`backend/src/Insolvia.Worker`), its own image so
  worker-only dependencies (9.6's PDF assembly, 9.7's model SDK) never land
  in the request path's image (ADR 0015's rule). It deploys inside
  `api-<env>.yml` — build/promote alongside the api image, then
  `update-function-code` on the worker — and the `record` job stamps its
  `sha-<commit>` tag like every other repo. No alias, no version dance:
  a queue consumer has no router to protect (the module header owns the
  argument).
- **Grant seams, both by role NAME** (the case_store pattern — and the
  mailer's, in insolvia-services — so no
  module references another's resources): this module attaches
  `sqs:SendMessage` onto the API role it is passed, and `modules/case_store`
  attaches the worker's tax-id key grant onto the worker role it is passed
  (`worker_role_name`). The queue URL reaches the API as
  `/insolvia/<env>/api/job-queue-url` → `JOB_QUEUE_URL`; the worker reads its
  own namespace (the case key alias among it).
- **The queue carries identifiers only** — job id, case id, kind; never case
  data — which is why SSE-SQS suffices and the case CMK is deliberately not
  used (a queue under that key would also trip `DenyCaseDataDecryption` for
  the deploy role). Retry is `maxReceiveCount = 3` into the DLQ; the DLQ
  depth alarm is the pipeline's real pager, publishing to the API's existing
  alarms topic.
- **In `infra/envs/dev` the worker half is absent** (`ecr_repository_url =
  null` — there is no Lambda in dev): the module creates the queue + DLQ
  only. A local host enqueues onto that real queue and the worker's local
  poller (`dotnet run --project backend/src/Insolvia.Worker`) consumes it
  under the developer's own credentials, so the whole loop runs on a laptop; the one
  cloud-only piece is the managed SQS→Lambda event source mapping itself.
- **First apply in a fresh environment** hits the usual image-before-apply
  deadlock: `scripts/bootstrap-ecr-images.sh <env> worker` seeds
  `insolvia-shared-worker-dotnet:<env>` after `shared` applies, then apply the
  env. (The Python worker image was `insolvia-shared-jobs`.)

## Auth (`infra/modules/auth/`) — the consumer half

**The Cognito pools are not in this repo.** The pool for each environment
(`insolvia-<env>-users`), its hosted domains (the prefix
`insolvia-<env>-auth.auth.us-east-1.amazoncognito.com`, and on staging
`staging-auth.insolvia.ai`, `dev-auth.insolvia.ai`), its app clients (`-app`,
`-portal`, `-mcp-<harness>`) and the sign-in page's style are
insolvia-services' `infra/modules/auth`. They are
applied from its `infra/envs/{staging,prod}`, and — for development — as ONE
shared pool, `insolvia-dev-users`, from its `infra/envs/dev`
([ADR 0026](../adr/0026-auth-is-a-shared-service.md)). That module's header
owns the pool's configuration and the reasons behind it: self-signup off,
case-insensitive usernames, the password policy, managed login, callback
origins, the custom domain's apex dependency, token lifetimes. Changing any of
these, or adding a client, is a PR there, released there, before any PR here
that reads the result.

`modules/auth` here is still called as `module "auth"` and keeps its output
names (`user_pool_id`, `user_pool_arn`, `web_client_id`, `portal_client_id`,
`domain`, `issuer_url`, `mcp_client_ids`, `mcp_client_ids_by_harness`). Every
env root, SSM parameter and deploy workflow reads it exactly as before. It
does two things:

- **Reads the pool's identifiers from SSM**, through `modules/auth_contract`:
  `/insolvia/<env>/auth/{user-pool-id,user-pool-arn,client-id,portal-client-id,domain,issuer-url,mcp-client-ids}`,
  published by insolvia-services (the pattern humbugg uses for its shared
  dev pool). A missing parameter fails the plan with its name. `domain` is
  the custom domain wherever one exists, decided there, so a new one moves the
  app on its next build with no change here; `mcp-client-ids` is the MCP
  allowlist, so a harness registered there is allowed here.
- **Grants this repo's Lambda roles their one action** on the pool
  (`aws_iam_role_policy.api_invite` / `.admin_invite`: `AdminCreateUser`,
  scoped to the pool's ARN). The module header explains why it is one action.

The API **verifies** tokens. The env roots publish `auth_issuer_url` and the
client ids into `/insolvia/<env>/api/`, the deploy workflow derives them into
`AUTH_ISSUER_URL` / `AUTH_CLIENT_ID` / `AUTH_PORTAL_CLIENT_ID`, and the
API validates the issuer, `token_use == "access"` and `client_id`
against the pool's JWKS. Auth fails **closed**: missing config gives a 401 on
every protected route, never a bypass.

## Case data key (`infra/modules/case_store/`)

The customer-managed KMS key for GLBA-scope case data — SSNs, full
financials, the source documents — so the posture here is the one to copy,
not to improvise on. The logical model is
[`case-data-model.md`](case-data-model.md); since ADR 0028 the rows are
Aurora's (`modules/aurora`, under its own key), and this module is the key the
**sealed tax ids and the case documents** are protected by. It held the
DynamoDB case and access-log tables until plan 18.23 deleted them; it keeps
its name because renaming the call would move the key's address, and
Terraform would destroy and re-create the key. One key per environment —
`alias/insolvia-staging-cases`, `alias/insolvia-prod-cases`, and
`alias/insolvia-dev-<short-id>-cases` on each developer machine. Local is the
same module, not an approximation of it, so a KMS or IAM mistake surfaces on
a laptop instead of after a deploy.

**Encryption at rest.** A customer-managed key per environment, rotation
enabled. Staging and prod never share a key: the key is what makes prod data
unreadable to a staging mistake. The document bucket
(`modules/case_documents`) takes this key's ARN rather than minting a second
one, so one key protects one case.

**Who can use the key.** Disjoint grants, each by role NAME from this module's
side (or the S3 module's):

| Principal | Key |
|---|---|
| API Lambda role | `GenerateDataKey`/`Decrypt` **directly**, fenced to `kms:EncryptionContext:purpose = debtor-tax-id` — the tax-id envelope (13.12); the S3-fenced pair for documents (`modules/case_documents`) |
| Pipeline worker role | `Decrypt` alone under the tax-id context (it prints B121, it never enters an identifier); the S3-fenced pair for packets |
| Filing worker role | `Decrypt` alone under the tax-id context (the Case Upload file); the S3-fenced pair for packets and receipts (`modules/filing_worker`) |
| MCP service role | Nothing — no tax-id context at all, so "no tool returns a full tax identifier" is an IAM fact |
| CI deploy role | Manage the key; **explicitly denied** `Decrypt`, `GenerateDataKey*` and `ReEncrypt*` except where SSM or CloudTrail is the calling service |

The tax-id statements are the one place a role touches the key without a
`kms:ViaService` fence, and the encryption context is what replaces it: a
call that does not carry `purpose = debtor-tax-id` is denied, and a call that
does can only be the envelope the hosts designed (`TaxIdKeyUse`). Every
deployed host is told the alias explicitly (`insolvia-kms-case-key-alias`),
and `backend/src/Insolvia.Aws/Sealing/SealingKeys.cs` refuses one not shaped
`alias/insolvia-<env>-cases` — the shape ci-trust's deny matches.
`infra/envs/dev` needs no grant: the developer's own IAM user reaches the key
through the key policy's root delegation.

**On the key policy, and "no human read paths in prod".** The key policy grants
the account root `kms:*` — AWS's default, and it means "IAM identity policies
decide", not "everyone can decrypt". The stricter alternative, naming only the
API role and omitting root, is what would make "no human read path" a property
of the key itself; it is rejected because a key policy naming no principal able
to change it is unrecoverable. The property is delivered instead by no human
principal holding KMS decrypt permissions, and by the deploy role's explicit
deny in insolvia-services' `infra/envs/ci-trust`. That is a weaker guarantee
honestly stated, rather than a stronger one that risks locking the account out
of its own data.

**Deletion.** Scheduling the key's deletion makes every sealed tax id and every
document permanently unreadable, so prod carries the maximum 30-day key
deletion window; staging the 7-day minimum, holding only synthetic data. The
key has no `prevent_destroy`, which looks like an omission and is not: it would
bind staging too — `prevent_destroy` takes a literal, not a variable — and
staging's disposability is the point; a scheduled deletion is cancellable for
the whole window.

**The access log** — which signed-in user read or changed which case — is the
Postgres `access_log` table since ADR 0028, append-only for every host
role (`backend/src/Insolvia.Migrator/Grants.cs`). It answers the question the
CloudTrail trail structurally cannot, because AWS only ever sees a host's role.

**Audit logging** lives next door, in `infra/modules/audit_trail/` — see below.

## Case-data audit trail (`infra/modules/audit_trail/`)

CloudTrail management events — which is where KMS key use lands — into an
`insolvia-<env>-audit` bucket under its own key; it also recorded DynamoDB
data events on the case and vault tables until those were deleted (18.23). One instance per deployed
environment; on a developer machine it is opt-in (below).

**What it can and cannot prove.** Because
[ADR 0001](../adr/0001-client-stays-dumb-trust-boundary.md) makes the API
Lambda's role the only application principal, every data event names
`insolvia-<env>-api-role` and never the signed-in user behind it. That makes
this evidence about **administrative** access — the "no human read paths in
prod" claim — and about the shape and volume of application access. *Which
user read this SSN* is a different question that only an application-level log
the API writes can answer. Do not let this trail stand in for that one.

**Its key is not the case key**, and that is forced rather than chosen:
`ci-trust`'s `DenyCaseDataDecryption` denies the deploy role
`kms:GenerateDataKey` on anything aliased `alias/insolvia-*-cases`, so a trail
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

**Scope and cost.** Management events are off by default in the module — they
would double the volume to say what the Terraform diff already says —
**except** that KMS `Decrypt`/`GenerateDataKey` ARE management events, and a
selector can only exclude KMS, never select it alone. So every root sets
`include_management_events = true`: it is the only way to retain each seal and
open of the vault and tax-id keys (with their encryption context) past Event
History's 90 days. `data_resource_arns` (DynamoDB data events, billed per
event) is empty everywhere since the tables went; a precondition refuses a
trail with neither. Retention is 90 days on staging and a year on prod.

## Filing-credential vault (`infra/modules/filing_credentials/`)

ADR 0024's guardrail 3: an attorney's PACER password and TOTP seed, sealed
under a **dedicated key** (`alias/insolvia-<env>-filing-credentials`); the
sealed rows are Aurora's `filing_credentials` table, SELECT for the `filing`
Postgres role alone (ADR 0028 — the DynamoDB table of the same name went in
18.23). The module's
header owns the reasoning; the facts a reader needs here:

- **The key policy, not IAM, keeps Decrypt to the filing worker's role**
  (`insolvia-<env>-filing-role`): an explicit `Deny kms:Decrypt` to every
  principal whose `aws:PrincipalArn` is not that role — the API's role, the
  deploy role, every admin — plus a deny of `ReEncrypt*`/`CreateGrant` to
  everyone and of any `Decrypt` outside the vault's encryption-context
  purpose. The API's role is allowed `kms:GenerateDataKey` alone: it seals
  and can never open. `terraform test` in the module pins the whole document.
- **The worker's role is created here**, ahead of the filing worker (ADR 0024
  PR 7), because KMS refuses a key policy naming a principal that does not
  exist. Here it holds Decrypt on the vault key under the vault purpose, and
  nothing else; `modules/filing_queue` and `modules/filing_worker` add its
  queue, bucket and kill-switch grants.
- **ci-trust's `DenyFilingCredentialDecryption`** (alias-matched, like
  `DenyCaseDataDecryption`) keeps the pipeline unable to open a credential even
  if it rewrote the key policy. It is hardening; the deploy needs no new grant.
- **The signed authorization (guardrail 2)** is Aurora's too, beside the
  credential rows; it needs nothing from this module.
- **Dev** passes `worker_assumable_by = [the developer]` so the open can be
  proved by assuming the real role; a precondition refuses that list in any
  environment not named `dev-*`.

**Locally** it is off by default: a per-developer trail bills to record a
developer reading their own synthetic cases. It can still be stood up on a
laptop, which is the part that matters —

```
terraform -chdir=infra/envs/dev apply -var=enable_audit_trail=true ...
```

— so a change to this module is exercised before staging meets it, rather
than after. Turn it back off and re-apply to remove it.

## Filing queue (`infra/modules/filing_queue/`)

ADR 0024's guardrail 1 says approval is the only producer of a filing job;
this is where the job goes — `insolvia-<env>-filing` and its `-dlq`, the
filing worker's own queue (ADR 0018's pattern, not the case-job pipeline's
queue). The module's header owns the reasoning; the facts:

- **Two grants, both by role NAME and both pinned whole by `terraform test`**:
  the API's role may `sqs:SendMessage` and nothing else
  (`insolvia-<env>-filing-enqueue`); the filing worker's role
  (`insolvia-<env>-filing-role`, from `modules/filing_credentials`) may
  receive, delete, change visibility and read attributes, and never send
  (`insolvia-<env>-filing-consume`). In code the one sender is
  `adapters/aws/filing_queue.py` and its one caller the approval
  (`tests/unit/test_filing_approval.py` reads the source tree to keep it so).
- **The message is ids only** — approval, filing and case id — so SSE-SQS is
  enough, for `job_pipeline`'s reason.
- **Retention is two approval lifetimes (2 h)** and the visibility timeout
  15 minutes: an approval expires after an hour, so an older job can never be
  consumed. DLQ 14 days, `maxReceiveCount = 3`.
- **The consumer is the filing worker** (`backend/src/Insolvia.Filing`) — the event source mapping is
  `modules/filing_worker`'s (below), batch size 1.
- The URL reaches the API as `/insolvia/<env>/api/filing-queue-url` →
  `FILING_QUEUE_URL`; dev publishes the same parameter and
  `scripts/dev-aws-setup.sh` writes the output into `backend/.env`.

## Filing worker (`infra/modules/filing_worker/`)

The filing worker (`backend/src/Insolvia.Filing`) as
`insolvia-<env>-filing-worker`, an image Lambda from the shared
`insolvia-shared-filing-dotnet` repository, consuming the filing queue at batch
size 1 and at most two concurrent invocations. The module header owns the
reasoning; the facts:

- **Every grant the worker role holds on case data is here, pinned whole by
  `terraform test`** (`insolvia-<env>-filing-worker-access` on
  `insolvia-<env>-filing-role`): `Decrypt` on the case key under the tax-id
  context; the case key through S3; `s3:GetObject` on `cases/*/packets/*` and
  `s3:PutObject` on `cases/*/filings/*`; `ssm:GetParameter` on the kill
  switch. The case data itself is Aurora rows, reached as the `filing`
  Postgres role. The vault's grants stay `filing_credentials`', the queue's
  `filing_queue`'s.
- **The kill switch** is `/insolvia/<env>/filing/submissions-enabled`, created
  `"false"` in every environment; Terraform ignores its value after that, so a
  human flips it without a deploy and no apply reverts the flip.
- **The Lambda's timeout (600 s) is the worker's claim lease** and sits under
  the queue's 900-second visibility timeout.
- **Three alarms** on the API's topic: DLQ depth, worker errors, and any
  `outcome_unknown` (a log metric filter) — the one state nobody may retry.
- **Dev**: the grants and the kill switch, no Lambda (`ecr_repository_url =
  null`); the local poller assumes the role.
- **First apply in a fresh environment** needs the image bootstrap:
  `scripts/bootstrap-ecr-images.sh <env> filing` after `shared` has created the
  repository (`insolvia-shared-filing-dotnet` since the switch).

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

What it owns is deliberately only what local dev consumes today. **The
database is not here**: local development runs Postgres 17 in Docker
(`backend/docker-compose.yml`, ADR 0028), migrated by the real migrator. Every
DynamoDB table this root held — the waitlist and admin audit, then the cases,
access log, firms and vault tables — went in 18.23 (the next
`dev-aws-setup.sh` on each machine destroys them).

- **Case key** via the same `modules/case_store` as staging/prod — this
  machine's customer-managed key, which the document bucket is encrypted
  under and tax ids are sealed with. The key deletion window is the minimum 7
  days, so a teardown does not leave a month-long pending key behind. `api_role_name` is null — there is no Lambda, so no
  grant is created and the developer's own credentials are the principal.
- **Auth is not per machine.** Every machine signs in against the ONE shared
  development pool, `insolvia-dev-users` (insolvia-services' `infra/envs/dev`,
  applied there by a human), read through `modules/auth_contract` from
  `/insolvia/dev/auth/*` — the same seam staging and prod read, so a laptop
  exercises it. `dev-aws-setup.sh` writes its ids into `backend/.env` and
  `apps/insolvia_app/.env`. The sign-in page's style is insolvia-services' too.
- **Job queue** via the same `modules/job_pipeline` as staging/prod, worker
  half absent (`ecr_repository_url = null` — dev has no Lambda).
  `insolvia-dev-<short-id>-jobs` (`JOB_QUEUE_URL` in `backend/.env`) is what
  the worker's local poller (`dotnet run --project
  backend/src/Insolvia.Worker`) consumes — the pipeline's local story, ADR
  0018.
- **Filing-credential vault** via the same `modules/filing_credentials` as
  staging/prod — the real key policy and the real filing worker's role, which
  in dev alone also trusts the developer, so the seal-only/open-only split can
  be proved on a laptop by assuming that role.
- **Filing queue** via the same `modules/filing_queue` (no API role — the
  developer sends, playing the API): `insolvia-dev-<short-id>-filing`
  (`FILING_QUEUE_URL` in `backend/.env`), which the filing worker's local
  poller (`dotnet run --project backend/src/Insolvia.Filing`) consumes.
- **Filing worker** via the same `modules/filing_worker`, without its Lambda:
  the grants attach to this machine's filing worker role
  (`filing_worker_role_arn`), so a run under that role is held to what the
  deployed worker holds. The end-to-end filing proof against the fake CM/ECF
  is the .NET filing suite's (`backend/tests/Insolvia.Filing.Tests`).

No ECR/Lambda/API Gateway (local dev runs the backend via compose or
`dotnet run`, not Lambda), and no IAM beyond the filing worker's role above —
the developer's own credentials are the principal. The local database is
**seeded, not empty**: `scripts/dev-aws-seed.sh` converges it on
`seeds/dev.json` — the dev account (in the shared pool), its firm and the
fixture cases it names, whose sample documents come from the committed
`seeds/fixtures` tree — through the migrator's `seed` command, the same
loader that seeds staging on every deploy
([ADR 0021](../adr/0021-test-tiers-and-seed-fixtures.md)).

**CI never touches this env.** It is applied and destroyed only by
`scripts/dev-aws-{setup,destroy}.sh` (`dev-aws-reset.sh` wipes the local
database and, with `--documents`, the bucket) (see `scripts/README.md`) with the
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
  (`INSOLVIA_API_BASE_URL` on the Lambda); the list lives in the API's
  database, and the marketing Lambda holds no AWS data-plane access
  (docs/adr/0001).
- **Image lifecycle**: Terraform creates the Lambda from
  `<ecr>:{var.marketing_image_tag}` (this env's moving marker tag) and then
  ignores `image_uri`; CI rolls images forward with
  `aws lambda update-function-code`. **First apply** needs the image to exist:
  release insolvia-services' `infra/envs/shared` (which creates the
  repository), push the image,
  then a full apply.

Names: `insolvia-shared-marketing` (ECR — shared across envs),
`insolvia-prod-marketing-ssr` (Lambda + HTTP API + role),
`insolvia-prod-marketing-us-east-1` (S3).

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
└──── insolvia-services ────┘   └── this repo ──┘
```
`ci-trust` must exist first: it holds the OIDC provider + deploy role that CI
*assumes* to apply everything else, and it is **human-applied only** because
the deploy role can't grant itself permissions (`DenySelfPrivilegeEscalation`).
Then `shared` (zone + cert + SES + container repositories), applied by
insolvia-services' own `release.yml`. Both are that repo's — its
[`docs/reference/terraform.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/reference/terraform.md) owns their
order and bootstrap. Here, CI applies `staging` on merge to `main`, and `prod`
behind the release's `insolvia-production` approval (or an out-of-band
`infra-prod.yml` dispatch).

The ordering is not ceremonial, for two independent reasons: every env looks
the cert up with `statuses = ["ISSUED"]`, **and** every env looks its container
repositories up with `data "aws_ecr_repository"`. Both hard-fail until `shared`
has applied. While both lived here, `release.yml` ran the shared apply as the
first leg of its staging stage so a consumer's lookup could never race the
apply that created it. **Across two repositories nothing orders them
automatically**, so the rule is a sequence of merges: a new service's
repository is a PR to insolvia-services, released there, *before* the PR here
that deploys it. The `insolvia-new-package` skill owns the steps.

Changing the deploy role's permissions is a `ci-trust` apply — a PR to
insolvia-services, then a human runs its `scripts/apply-ci-trust.sh`. This is
why adding an IAM permission the pipeline needs is never a "just merge it"
change; the `insolvia-deploy-role-permissions` skill owns the procedure.

**The mailer's staging/prod resources moved state too.** They were
`module.mailer` in this repo's `envs/{staging,prod}` and are now owned by
insolvia-services' own `infra/envs/{staging,prod}`
(`insolvia-services/<env>/terraform.tfstate`), moved by a human-run
`scripts/migrate-mailer-state.sh` per its
[`docs/runbooks/state-migration.md`](https://github.com/insolvia-ai/insolvia-services/blob/main/docs/runbooks/state-migration.md),
executed 2026-10-11. During that window both `main.tf` here carried a
`removed { from = module.mailer; lifecycle { destroy = false } }` safety net;
it was deleted once both states were confirmed to hold no `module.mailer`. This repo still writes the
API's `MAILER_API_URL` SSM parameter, as `https://${var.mailer_subdomain}`.

## Destruction safety

Tear down `prod`/`staging` here before anyone considers `shared` in
insolvia-services — it holds the hosted zone, the certificate and the
container repositories every environment here depends on.

## Conventions
- Resources: `insolvia-<env>-<component>-<identifier>` — environment SECOND
  (e.g. `insolvia-staging-api`, `insolvia-prod-jobs-dlq`). S3 buckets
  take a `-us-east-1` suffix for global uniqueness
  (`insolvia-prod-case-documents-us-east-1`). The `insolvia-aws-naming` skill
  owns the full per-resource-type table and the component-naming rule.
- Tags: `{ Project = "insolvia", Environment = <env>, ManagedBy = "terraform" }`.
- Sensitive vars `sensitive = true`; commit `terraform.tfvars.example`, never real `*.tfvars`.
- The infra directory is always `infra/`, never `terraform/`.
