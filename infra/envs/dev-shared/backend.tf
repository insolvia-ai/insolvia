terraform {
  backend "s3" {
    bucket = "insolvia-shared-terraform-state-us-east-1"
    # One key for every machine — this root is shared, unlike infra/envs/dev,
    # whose keys are insolvia/dev/<account-id>/<machine-id>/…. Deliberately
    # NOT under insolvia/dev/: scripts/dev-aws-destroy-orphan.sh sweeps that
    # prefix for per-machine states, and this is nobody's orphan.
    key     = "insolvia/dev-shared/terraform.tfstate"
    region  = "us-east-1"
    encrypt = true

    # Native S3 state locking: two machines running dev-aws-setup.sh at once
    # take turns here instead of racing on the same branding style.
    use_lockfile = true
  }
}
