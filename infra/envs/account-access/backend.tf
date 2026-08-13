terraform {
  backend "s3" {
    bucket  = "insolvia-shared-terraform-state-us-east-1"
    key     = "insolvia/account-access/terraform.tfstate"
    region  = "us-east-1"
    encrypt = true

    # Native S3 state locking (Terraform >= 1.10), same as every other root.
    # Like ci-trust this state is only ever applied by a human, so the lock
    # guards two people applying at once rather than a local-vs-CI race — but
    # the guarantee stays identical to every other environment.
    use_lockfile = true
  }
}
