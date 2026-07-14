# Production web hosting -> app.insolvia.ai
# References the shared zone + wildcard cert by name (not by remote state).

locals {
  environment = "prod"
  common_tags = {
    Project     = "insolvia"
    Environment = local.environment
    ManagedBy   = "terraform"
  }
}

data "aws_route53_zone" "main" {
  name = var.domain_name
}

data "aws_acm_certificate" "wildcard" {
  provider    = aws.us_east_1
  domain      = "*.${var.domain_name}"
  statuses    = ["ISSUED"]
  most_recent = true
}

module "web_hosting" {
  source = "../../modules/web_hosting"

  project             = "insolvia"
  environment         = local.environment
  domain_name         = var.subdomain
  hosted_zone_id      = data.aws_route53_zone.main.zone_id
  acm_certificate_arn = data.aws_acm_certificate.wildcard.arn
  tags                = local.common_tags
}
