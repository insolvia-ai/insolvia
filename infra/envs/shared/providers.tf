terraform {
  required_version = ">= 1.10"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
    # Used by module.inbound_forwarding to zip the forwarder Lambda source at
    # plan time, so the Lambda needs no separate CI build step.
    archive = {
      source  = "hashicorp/archive"
      version = "~> 2.4"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

# CloudFront/ACM must live in us-east-1. Kept explicit and aliased so the
# default region can change later without touching cert/CDN resources.
provider "aws" {
  alias  = "us_east_1"
  region = "us-east-1"
}
