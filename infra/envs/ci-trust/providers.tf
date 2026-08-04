terraform {
  required_version = ">= 1.10"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.37"
    }
  }
}

# IAM and the OIDC provider are global; the region only needs to be somewhere
# valid. us-east-1 matches everything else in this account. No us_east_1 alias
# here — this root has no ACM/CloudFront resources (those stay in `shared`).
provider "aws" {
  region = var.aws_region
}
