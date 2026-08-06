terraform {
  required_version = ">= 1.10"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.37"
    }
  }
}

# IAM is global; the region only needs to be somewhere valid, and us-east-1
# matches the rest of the account. No us_east_1 alias here — this root has no
# ACM/CloudFront resources.
provider "aws" {
  region = var.aws_region
}
