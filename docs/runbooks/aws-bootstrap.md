# AWS & GitHub bootstrap

One-time setup so CI can deploy Insolvia to AWS with **no long-lived keys**.
Insolvia runs in its **own dedicated AWS account** (`521762924626`). Resources
are namespaced by the `insolvia` project + environment.

> **Status (2026-07-23): bootstrap is complete and deploys are live.**
> `insolvia.ai` is registered, Gandi delegates to Route53 hosted zone
> `Z01038711J6IZ68FD6ZDW`, the state bucket exists, `infra/envs/shared` is
> applied, and the `*.insolvia.ai` ACM certificate is `ISSUED`. What follows is
> the runbook for standing this up again in a fresh account. Work steps 1 → 6
> in order; **step 3 (importing the hosted zone, #13) is not optional** —
> skipping it breaks certificate validation in a way that is hard to diagnose.

## 0. Prerequisites
- AWS CLI configured with credentials that can create S3/IAM/Route53/ACM in the Insolvia account.
- `terraform` `>= 1.10` (native S3 state locking — `use_lockfile`), `tflint`.
- Admin access to the `insolvia-ai/insolvia` GitHub repo (to add secrets + branch protection).

### Running Terraform locally — export credentials first

A working `aws` CLI is **not** enough for Terraform: the failure looks like
having no credentials at all, and it is easy to misdiagnose. The mechanism, the
three failure modes and the exact fix for each belong to the
**`insolvia-aws-auth` skill** — read it rather than improvising. The one line
every manual apply below needs, in the same shell:

```bash
eval "$(aws configure export-credentials --format env)"
```

### The ci-trust anchor

`infra/envs/ci-trust` owns the GitHub OIDC provider, the
`insolvia-shared-deploy-role` deploy role, and that role's permissions policy. It is
**applied only by a human admin — never by CI — and that is the point**: the
role's own policy denies it `iam:PutRolePolicy` on itself, so a privilege change
cannot take effect from merged code alone. Because CI never applies `ci-trust`,
there is deliberately **no `ci-trust-*.yml` workflow**; everything else
(`shared`, `staging`, `prod`) is CI-applied as normal.

Why the self-deny exists is
[`../reference/terraform.md`](../reference/terraform.md#deployment-order).
Changing the role's permissions — including the `AccessDenied`-on-a-new-action
symptom that means you need to — belongs to the
**`insolvia-deploy-role-permissions` skill**. The apply itself is one of the
only legitimate local applies:

```bash
scripts/apply-ci-trust.sh
```

### Human IAM users

The admin user this runbook's steps are executed *as* is itself codified, in
`infra/envs/account-access` — also human-applied, and in a fresh account it is
the one root that must be stood up by whoever holds the root credentials, since
there is no admin user yet to run anything else. It is otherwise outside the
apply order: nothing depends on it. See
[`../reference/terraform.md`](../reference/terraform.md#human-account-access)
for what it does and does not model, and
[`iam-mfa-rotation.md`](iam-mfa-rotation.md) for MFA, which is not a Terraform
resource on purpose.

```bash
scripts/apply-account-access.sh
```

## 1. Terraform state bucket — the first action in the entire plan
Every `backend.tf` in the repo points at this bucket, so `terraform init` cannot
run anywhere until it exists. Verified absent 2026-07-21.
```bash
aws s3api create-bucket --bucket insolvia-shared-terraform-state-us-east-1 --region us-east-1
aws s3api put-bucket-versioning --bucket insolvia-shared-terraform-state-us-east-1 \
  --versioning-configuration Status=Enabled
aws s3api put-bucket-encryption --bucket insolvia-shared-terraform-state-us-east-1 \
  --server-side-encryption-configuration \
  '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
aws s3api put-public-access-block --bucket insolvia-shared-terraform-state-us-east-1 \
  --public-access-block-configuration \
  BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
```
State keys: `insolvia/{shared,staging,prod}/terraform.tfstate`.

## 2. GitHub OIDC provider (created by `ci-trust`)
The Insolvia account has no GitHub OIDC provider yet — so `infra/envs/ci-trust`
**creates** it (step 4). There is exactly one such provider per account.
Confirm it is absent before first apply (empty list is expected):
```bash
aws iam list-open-id-connect-providers
```
If you later consolidate into an account that already has the provider, switch
the `aws_iam_openid_connect_provider.github` resource back to a `data` source.

## 3. ⚠️ Import the existing hosted zone — BEFORE any apply on `shared`
The hosted zone for `insolvia.ai` (`Z01038711J6IZ68FD6ZDW`) already exists, holds
only its NS + SOA records, and is the zone Gandi delegates to — but it was
created outside Terraform, and with no state bucket there was never a state file.

`infra/envs/shared/main.tf` declares `resource "aws_route53_zone" "main"`. Applied
against empty state, that creates a **second** hosted zone for `insolvia.ai`.
Route53 permits duplicate zones and gives the new one different nameservers, so:

1. Gandi still delegates to the *original* zone — the Terraform-managed zone is
   authoritative for nothing.
2. ACM DNS-validation records land in the new, unreferenced zone, so validation
   never completes.
3. `aws_acm_certificate_validation` hangs until timeout and surfaces as a
   certificate error that points nowhere near the real cause.
4. You pay for both zones.

Import instead of recreating — this keeps Gandi's delegation valid, with no
registrar change:
```bash
cd infra/envs/shared
terraform init
terraform import aws_route53_zone.main Z01038711J6IZ68FD6ZDW
terraform plan   # MUST NOT propose creating a hosted zone
```
**Do not skip the plan check.** A plan that proposes creating an
`aws_route53_zone` means the import did not take — stop and fix it before
applying.

## 4. Apply the trust anchor, then shared infra

The OIDC provider + deploy role live in `infra/envs/ci-trust` (human-applied
only — see [§ The ci-trust anchor](#the-ci-trust-anchor)); the
zone + cert + SES live in `infra/envs/shared`. Apply ci-trust first, because
`shared` (and everything after) is applied *as* the deploy role ci-trust
creates.

```bash
terraform -chdir=infra/envs/ci-trust apply   # OIDC provider + insolvia-shared-deploy-role role
terraform -chdir=infra/envs/ci-trust output github_actions_role_arn
```

```bash
terraform -chdir=infra/envs/shared apply      # adopts the imported zone, creates the *.insolvia.ai cert + SES
```
Because delegation is already in place, DNS validation should resolve and the
certificate should reach `ISSUED` without any registrar work.

## 5. Wire the GitHub repo
```bash
# Deploy role ARN from step 4:
gh secret set AWS_ROLE_ARN --repo insolvia-ai/insolvia --body "arn:aws:iam::521762924626:role/insolvia-shared-deploy-role"
```
Repo lockdown (private, branch protection, environments) is documented in the
plan §2e and applied once `@insolvia-dev` has admin on the repo.

## 6. Confirm delegation and the certificate
`insolvia.ai` is registered and Gandi already points at the imported zone. Verify
the registrar's nameservers still match the zone Terraform now manages:
```bash
terraform -chdir=infra/envs/shared output route53_name_servers
dig +short NS insolvia.ai
```
Once those agree and the ACM cert reports `ISSUED`, the env pipelines work:
`staging` deploys automatically on merge to `main`; `prod` is dispatched
manually. (Before the cert issues, every env-level `terraform plan` fails —
each env looks the cert up with `statuses = ["ISSUED"]` — so there is nothing
to switch on; the ordering itself is the gate.)

## 7. Cognito email service-linked role — before any pool sends through SES

One-time, account-wide, human-run. The first pool with `email_configuration`
in DEVELOPER mode (`ses_source_arn` on `modules/auth` / `modules/staff_auth`,
#210) needs `AWSServiceRoleForAmazonCognitoIdpEmailService` to exist, and the
CI deploy role deliberately cannot create service-linked roles for Cognito
(its `iam:CreateServiceLinkedRole` grant covers API Gateway only) — so a
staging apply that carries the first DEVELOPER-mode pool fails without this:

```bash
aws iam create-service-linked-role --aws-service-name email.cognito-idp.amazonaws.com
```

Idempotent in effect: a second run fails with `InvalidInput` naming the role
as already taken, which is the confirmation. Only the Cognito service itself
can assume it — creating it grants nothing to any human or CI principal.
*Done for account `521762924626` on 2026-08-10.*

## Order of operations
1 (state bucket) → 2 (confirm no OIDC provider) → **3 (import the hosted zone)**
→ 4 (apply `shared`) → 5 (secrets) → 6 (verify delegation + cert) →
apply `staging` / `prod` envs.
