terraform {
  backend "s3" {
    bucket  = "insolvia-shared-terraform-state-us-east-1"
    key     = "insolvia/ci-trust/terraform.tfstate"
    region  = "us-east-1"
    encrypt = true

    # Native S3 state locking (Terraform >= 1.10), same as every other root.
    # This state is applied only by a human (there is no ci-trust-*.yml
    # workflow), so the lock guards against two people applying at once rather
    # than a local-vs-CI race — but it costs nothing to keep the guarantee
    # identical to the other environments.
    use_lockfile = true
  }
}
