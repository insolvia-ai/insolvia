# Same state bucket as every other env, but NO `key` here: each developer
# machine owns its own state, so the key is injected at init time by
# scripts/dev-aws-common.sh —
#
#   -backend-config="key=insolvia/dev/<account-id>/<machine-id>/terraform.tfstate"
#
# CI's offline `terraform init -backend=false` (shared-infra-plan.yml) never
# needs the key, so validation works without it.
terraform {
  backend "s3" {
    bucket  = "insolvia-terraform-state"
    region  = "us-east-1"
    encrypt = true

    # Native S3 state locking (Terraform >= 1.10) — see the shared backend for
    # the rationale. Locks a <injected-key>.tflock object per machine.
    use_lockfile = true
  }
}
