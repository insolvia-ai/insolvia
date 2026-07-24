# AWS & GitHub bootstrap

One-time setup so CI can deploy Insolvia to AWS with **no long-lived keys**.
Insolvia runs in its **own dedicated AWS account** (`521762924626`), separate
from `andreas-services`. Resources are still namespaced by the `insolvia`
project + environment.

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

**A working `aws` CLI is not enough for Terraform**, and the failure looks like
having no credentials at all:

```
Error: No valid credential sources found
Error: failed to refresh cached credentials, no EC2 IMDS role found, …
```

`aws sts get-caller-identity` succeeding while `terraform plan` says this is the
signature. The cause is that `~/.aws/config` authenticates with the newer
`login_session` (`aws login`) format, which is an **AWS CLI mechanism** —
Terraform's Go SDK does not implement it, finds nothing in the standard chain,
falls through to EC2 instance metadata, and times out.

Resolve the session into the env vars every SDK understands, in the same shell:

```bash
eval "$(aws configure export-credentials --format env)"
```

Use `eval` rather than running it bare: the output *is* the secret. The
credentials are short-lived, so re-run it when a session expires. This is the
same step `scripts/dev-aws-common.sh`'s `export_temporary_aws_credentials` does
for the per-developer AWS layer, hoisted here because manual applies (below)
need it too.

### When a local apply is *required* rather than convenient

Normally every apply runs in CI through the OIDC deploy role. There is one
deliberate exception: **`infra/envs/shared` cannot change the deploy role's own
permissions from CI.** An explicit `Deny` (`DenySelfPrivilegeEscalation` in
`infra/envs/shared/main.tf`) blocks `iam:PutRolePolicy` on the role itself, and
explicit deny beats every allow — so any PR that edits
`aws_iam_role_policy.github_permissions` makes `shared-infra-deploy.yml` fail
with `AccessDenied`, and every env apply that depends on the new grant fails
after it.

That is the control working, not a bug: a privilege change must be applied by a
human with their own credentials, so a code diff alone can never grant the
pipeline more power. The recipe:

```bash
eval "$(aws configure export-credentials --format env)" && terraform -chdir=infra/envs/shared init -input=false && terraform -chdir=infra/envs/shared plan
```

Read the plan — it should touch `aws_iam_role_policy.github_permissions` and
nothing else — then `apply`. Doing this *before* merging the PR keeps CI green,
because the post-merge apply then sees no diff and never calls
`iam:PutRolePolicy`.

## 1. Terraform state bucket — the first action in the entire plan
Every `backend.tf` in the repo points at this bucket, so `terraform init` cannot
run anywhere until it exists. Verified absent 2026-07-21.
```bash
aws s3api create-bucket --bucket insolvia-terraform-state --region us-east-1
aws s3api put-bucket-versioning --bucket insolvia-terraform-state \
  --versioning-configuration Status=Enabled
aws s3api put-bucket-encryption --bucket insolvia-terraform-state \
  --server-side-encryption-configuration \
  '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
aws s3api put-public-access-block --bucket insolvia-terraform-state \
  --public-access-block-configuration \
  BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
```
State keys: `insolvia/{shared,staging,prod}/terraform.tfstate`.

## 2. GitHub OIDC provider (created by `shared`)
The Insolvia account has no GitHub OIDC provider yet — so `infra/envs/shared`
**creates** it (step 4). There is exactly one such provider per account.
Confirm it is absent before first apply (empty list is expected):
```bash
aws iam list-open-id-connect-providers --profile insolvia
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

## 4. Apply shared infra (creates the deploy role, cert)
```bash
cd infra/envs/shared
terraform apply    # adopts the imported insolvia.ai zone, creates the
                   # *.insolvia.ai ACM cert, and the insolvia-github-actions
                   # IAM role trusting repo:insolvia-ai/insolvia:*
terraform output github_actions_role_arn
```
Because delegation is already in place, DNS validation should resolve and the
certificate should reach `ISSUED` without any registrar work.

## 5. Wire the GitHub repo
```bash
# Deploy role ARN from step 4:
gh secret set AWS_ROLE_ARN --repo insolvia-ai/insolvia --body "arn:aws:iam::521762924626:role/insolvia-github-actions"
```
Repo lockdown (private, branch protection, environments) is documented in the
plan §2e and applied once `@ansavva` has admin on the repo.

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

## Order of operations
1 (state bucket) → 2 (confirm no OIDC provider) → **3 (import the hosted zone)**
→ 4 (apply `shared`) → 5 (secrets) → 6 (verify delegation + cert) →
apply `staging` / `prod` envs.
