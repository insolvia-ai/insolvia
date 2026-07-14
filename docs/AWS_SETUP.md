# AWS & GitHub bootstrap

One-time setup so CI can deploy Insolvia to AWS with **no long-lived keys**.
Insolvia reuses the **shared AWS account** (also home to `andreas-services`) and
is namespaced by the `insolvia` project + environment.

> ⚠️ **Status:** deploys are **gated off** until `insolvia.ai` DNS is live. You
> can complete steps 1–4 now; step 5 (domain) is the current blocker.

## 0. Prerequisites
- AWS CLI configured with credentials that can create S3/IAM/Route53/ACM in the shared account.
- `terraform` `~> 1.5`, `tflint`.
- Admin access to the `Insolvia-AI/insolvia` GitHub repo (to add secrets + branch protection).

## 1. Terraform state bucket (dedicated, for project isolation)
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
Insolvia runs in its own dedicated AWS account (`521762924626`), which has no
GitHub OIDC provider yet — so `infra/envs/shared` **creates** it (step 3). There
is exactly one such provider per account. Confirm it is absent before first
apply (empty list is expected):
```bash
aws iam list-open-id-connect-providers --profile insolvia
```
If you later consolidate into an account that already has the provider, switch
the `aws_iam_openid_connect_provider.github` resource back to a `data` source.

## 3. Apply shared infra (creates the deploy role, zone, cert)
```bash
cd infra/envs/shared
terraform init
terraform apply    # creates Route53 zone for insolvia.ai, *.insolvia.ai ACM cert,
                   # and the github-actions-insolvia IAM role trusting
                   # repo:Insolvia-AI/insolvia:*
terraform output github_actions_role_arn
```

## 4. Wire the GitHub repo
```bash
# Deploy role ARN from step 3:
gh secret set AWS_ROLE_ARN --repo Insolvia-AI/insolvia --body "arn:aws:iam::<acct>:role/github-actions-insolvia"

# Keep deploys off until DNS is live:
gh variable set DEPLOY_ENABLED --repo Insolvia-AI/insolvia --body "false"
```
Repo lockdown (private, branch protection, environments) is documented in the
plan §2e and applied once `@ansavva` has admin on the repo.

## 5. Domain / DNS  ← current blocker
`insolvia.ai` must be registered and delegated to the Route53 hosted zone created
in step 3. Point the registrar's nameservers at:
```bash
terraform -chdir=infra/envs/shared output route53_name_servers
```
Once DNS resolves and the ACM cert validates (`ISSUED`), flip deploys on:
```bash
gh variable set DEPLOY_ENABLED --repo Insolvia-AI/insolvia --body "true"
```
Then `staging` deploys automatically on merge to `main`; `prod` is dispatched
manually.

## Order of operations
1 → 2 (confirm) → 3 (shared) → 4 (secrets) → **5 (domain, blocked)** → enable
deploys → apply `staging` / `prod` envs.
