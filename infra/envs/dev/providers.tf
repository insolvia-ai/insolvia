terraform {
  required_version = ">= 1.10"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

# No aliased us_east_1 provider here, unlike the deployed envs: dev fronts
# nothing with CloudFront or an ACM cert, so there is no lookup that needs it.
