terraform {
  backend "s3" {
    bucket  = "insolvia-terraform-state"
    key     = "insolvia/shared/terraform.tfstate"
    region  = "us-east-1"
    encrypt = true

    # Native S3 state locking (Terraform >= 1.10) — a <key>.tflock object in
    # this same bucket, no DynamoDB table. Belt to the concurrency groups'
    # braces: CI serializes applies per env, and this makes any apply that
    # slips through (a local run alongside CI, a manual override) fail-fast on
    # the lock instead of racing the state file.
    use_lockfile = true
  }
}
