# Account-wide, environment-independent resources for Insolvia:
#   • Route53 hosted zone for insolvia.ai
#   • wildcard ACM cert *.insolvia.ai (+ apex SAN), DNS-validated, us-east-1
#   • the account-level GitHub OIDC provider + github-actions-insolvia role
#
# Insolvia has its own dedicated AWS account (521762924626), so this config
# creates the GitHub OIDC provider itself (see below).

locals {
  common_tags = {
    Project     = "insolvia"
    Environment = "shared"
    ManagedBy   = "terraform"
  }
}

# ── DNS zone ────────────────────────────────────────────────────
# Register insolvia.ai (blocked on the domain support request), then delegate
# the registrar to this zone's name servers (see outputs).
resource "aws_route53_zone" "main" {
  name = var.domain_name
  tags = local.common_tags
}

# ── Wildcard TLS certificate (us-east-1 for CloudFront) ─────────
resource "aws_acm_certificate" "wildcard" {
  provider                  = aws.us_east_1
  domain_name               = "*.${var.domain_name}"
  subject_alternative_names = [var.domain_name]
  validation_method         = "DNS"
  tags                      = local.common_tags

  lifecycle {
    create_before_destroy = true
  }
}

resource "aws_route53_record" "cert_validation" {
  for_each = {
    for dvo in aws_acm_certificate.wildcard.domain_validation_options :
    dvo.domain_name => {
      name   = dvo.resource_record_name
      type   = dvo.resource_record_type
      record = dvo.resource_record_value
    }
  }

  zone_id         = aws_route53_zone.main.zone_id
  name            = each.value.name
  type            = each.value.type
  records         = [each.value.record]
  ttl             = 60
  allow_overwrite = true
}

resource "aws_acm_certificate_validation" "wildcard" {
  provider                = aws.us_east_1
  certificate_arn         = aws_acm_certificate.wildcard.arn
  validation_record_fqdns = [for r in aws_route53_record.cert_validation : r.fqdn]
}

# ── GitHub Actions OIDC deploy role ─────────────────────────────
# This is a dedicated Insolvia AWS account, so the account-level GitHub OIDC
# provider does not exist yet — create it here. (There is exactly one such
# provider per account; if you later consolidate accounts, switch this back to
# a `data` source.) AWS validates the token against its own trusted CA store,
# but the API still requires at least one thumbprint.
resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
  thumbprint_list = [
    "6938fd4d98bab03faadb97b34396831e3780aea1",
    "1c58a3a8518e8759bf075b76b750d4f2df264fcd",
  ]
  tags = local.common_tags
}

data "aws_iam_policy_document" "github_assume" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    effect  = "Allow"

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values   = ["repo:${var.github_repo}:*"]
    }
  }
}

resource "aws_iam_role" "github_actions" {
  name               = "github-actions-insolvia"
  assume_role_policy = data.aws_iam_policy_document.github_assume.json
  tags               = local.common_tags
}

# Permissions the deploy pipeline needs: manage the static-hosting stack, read
# the shared zone/cert, and push build artifacts. Scoped to Insolvia resources.
data "aws_iam_policy_document" "github_permissions" {
  statement {
    sid = "TerraformState"
    actions = [
      "s3:ListBucket",
      "s3:GetObject",
      "s3:PutObject",
      "s3:DeleteObject",
    ]
    resources = [
      "arn:aws:s3:::insolvia-terraform-state",
      "arn:aws:s3:::insolvia-terraform-state/*",
    ]
  }

  statement {
    sid = "WebHostingBuckets"
    actions = [
      "s3:*",
    ]
    resources = [
      "arn:aws:s3:::insolvia-web-*",
      "arn:aws:s3:::insolvia-web-*/*",
    ]
  }

  statement {
    sid = "EdgeAndDns"
    actions = [
      "cloudfront:*",
      "route53:*",
      "acm:*",
    ]
    resources = ["*"]
  }

  statement {
    sid = "DeployRoleManagement"
    actions = [
      "iam:GetRole",
      "iam:PassRole",
      "iam:ListRolePolicies",
      "iam:GetRolePolicy",
    ]
    resources = [aws_iam_role.github_actions.arn]
  }
}

resource "aws_iam_role_policy" "github_permissions" {
  name   = "insolvia-deploy"
  role   = aws_iam_role.github_actions.id
  policy = data.aws_iam_policy_document.github_permissions.json
}
