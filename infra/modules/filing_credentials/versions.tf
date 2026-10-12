# Floors, not pins: the env roots own the exact provider build (their
# committed .terraform.lock.hcl). The aws floor matches the repo-wide 6.37
# reasoning in infra/CLAUDE.md.
terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 6.37"
    }
  }
}
