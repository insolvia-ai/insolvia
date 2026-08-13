terraform {
  backend "s3" {
    bucket  = "insolvia-shared-terraform-state-us-east-1"
    key     = "insolvia/staging/terraform.tfstate"
    region  = "us-east-1"
    encrypt = true

    # Native S3 state locking (Terraform >= 1.10) — see the shared backend for
    # the rationale. Belt to the concurrency groups' braces.
    use_lockfile = true
  }
}
