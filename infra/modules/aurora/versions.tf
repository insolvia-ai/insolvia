# Floors, not pins: the env roots own the exact provider build (their
# committed .terraform.lock.hcl). The aws floor matches the repo-wide 6.37
# reasoning in infra/CLAUDE.md — managed login branding needs >= 6.12, and
# 6.37 is where key_schema handling stopped eating indexes.
terraform {
  required_version = ">= 1.10"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 6.37"
    }
  }
}
