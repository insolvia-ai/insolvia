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

# The names the certificate covers, derived from the variable rather than from
# the certificate resource. This is load-bearing: `for_each` KEYS must be known
# at plan time, and `domain_validation_options` does not exist until the cert
# has been created. Keying off it means a fresh state cannot plan at all —
# "Invalid for_each argument ... known only after apply" — which blocks both the
# first apply and `terraform import`. Keys static, values resolved at apply.
locals {
  cert_domain_names = toset(["*.${var.domain_name}", var.domain_name])

  cert_validation = {
    for dvo in aws_acm_certificate.wildcard.domain_validation_options :
    dvo.domain_name => dvo
  }
}

resource "aws_route53_record" "cert_validation" {
  for_each = local.cert_domain_names

  zone_id = aws_route53_zone.main.zone_id
  name    = local.cert_validation[each.key].resource_record_name
  type    = local.cert_validation[each.key].resource_record_type
  records = [local.cert_validation[each.key].resource_record_value]
  ttl     = 60

  # A wildcard cert and its apex SAN validate through the SAME DNS record, so
  # both instances UPSERT identical content. Overwrite is required, not lax.
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

  # READ-ONLY on the pipeline's own role, and deliberately so.
  #
  # This environment manages the OIDC provider, this role, and this policy —
  # the three things that authorize CI in the first place. Terraform must be
  # able to REFRESH them or no plan can complete (that is what these actions
  # buy). It must NOT be able to MODIFY them: iam:PutRolePolicy here would let
  # any change to this file grant the pipeline admin, applied by the pipeline
  # itself, with a code diff as the only control.
  #
  # Consequence, and it is intended: a change to the deploy role, its policy,
  # or the OIDC provider makes the CI apply fail with AccessDenied. Privilege
  # changes require a human running apply with their own credentials. Everything
  # else in `shared` — zone, cert, DNS, email — still applies from CI normally.
  statement {
    sid = "DeployRoleSelfRead"
    actions = [
      "iam:GetRole",
      "iam:PassRole",
      "iam:ListRolePolicies",
      "iam:GetRolePolicy",
      "iam:ListAttachedRolePolicies",
      "iam:ListRoleTags",
    ]
    resources = [aws_iam_role.github_actions.arn]
  }

  # `infra/envs/shared` MANAGES the OIDC provider, so every apply must refresh
  # it. Without this, a CI apply dies on:
  #   AccessDenied: ... not authorized to perform: iam:GetOpenIDConnectProvider
  # Read and tag only — deliberately NOT DeleteOpenIDConnectProvider. This
  # provider is what lets CI authenticate at all; a pipeline that can delete its
  # own trust anchor can lock everyone out with no way back in via CI.
  statement {
    sid = "OidcProviderRead"
    actions = [
      "iam:GetOpenIDConnectProvider",
      "iam:ListOpenIDConnectProviders",
      "iam:ListOpenIDConnectProviderTags",
    ]
    resources = [aws_iam_openid_connect_provider.github.arn]
  }

  # ── Email stack (issues 1.5–1.11) ──────────────────────────────
  # SES identity/DKIM/MAIL FROM and receipt rules are account-and-region scoped;
  # most SES actions do not support resource-level ARNs, so this is "*" by
  # necessity rather than by laziness.
  statement {
    sid       = "SimpleEmailService"
    actions   = ["ses:*"]
    resources = ["*"]
  }

  # The inbound-mail bucket does not match the insolvia-web-* prefix above.
  statement {
    sid     = "InboundMailBucket"
    actions = ["s3:*"]
    resources = [
      "arn:aws:s3:::insolvia-inbound-mail-*",
      "arn:aws:s3:::insolvia-inbound-mail-*/*",
    ]
  }

  statement {
    sid       = "ForwarderCompute"
    actions   = ["lambda:*"]
    resources = ["arn:aws:lambda:*:${data.aws_caller_identity.current.account_id}:function:insolvia-*"]
  }

  statement {
    sid       = "ForwarderQueues"
    actions   = ["sqs:*"]
    resources = ["arn:aws:sqs:*:${data.aws_caller_identity.current.account_id}:insolvia-*"]
  }

  statement {
    sid       = "AlertTopics"
    actions   = ["sns:*"]
    resources = ["arn:aws:sns:*:${data.aws_caller_identity.current.account_id}:insolvia-*"]
  }

  # CloudWatch alarm APIs do not support resource-level permissions.
  statement {
    sid = "Alarms"
    actions = [
      "cloudwatch:PutMetricAlarm",
      "cloudwatch:DeleteAlarms",
      "cloudwatch:DescribeAlarms",
      "cloudwatch:ListTagsForResource",
      "cloudwatch:TagResource",
      "cloudwatch:UntagResource",
    ]
    resources = ["*"]
  }

  statement {
    sid       = "LambdaLogGroups"
    actions   = ["logs:*"]
    resources = ["arn:aws:logs:*:${data.aws_caller_identity.current.account_id}:log-group:/aws/lambda/insolvia-*"]
  }

  # The forward-to destination lives here as a SecureString. Terraform creates
  # the parameter but never owns its value (lifecycle ignore_changes).
  statement {
    sid       = "Parameters"
    actions   = ["ssm:*"]
    resources = ["arn:aws:ssm:*:${data.aws_caller_identity.current.account_id}:parameter/insolvia/*"]
  }

  # The forwarder Lambda needs its own execution role, so the pipeline must be
  # able to create roles — scoped to the insolvia-* name prefix.
  statement {
    sid = "ServiceRoleManagement"
    actions = [
      "iam:CreateRole",
      "iam:DeleteRole",
      "iam:GetRole",
      "iam:PassRole",
      "iam:TagRole",
      "iam:UntagRole",
      "iam:PutRolePolicy",
      "iam:GetRolePolicy",
      "iam:DeleteRolePolicy",
      "iam:ListRolePolicies",
      "iam:ListAttachedRolePolicies",
      "iam:ListInstanceProfilesForRole",
    ]
    resources = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/insolvia-*"]
  }

  # Attaching AWS-managed policies is constrained to the single policy the
  # forwarder actually uses. Without this condition the pipeline could attach
  # AdministratorAccess to a role it creates and then pass it to a Lambda —
  # a straightforward privilege-escalation path out of a scoped deploy role.
  statement {
    sid = "ServiceRolePolicyAttachment"
    actions = [
      "iam:AttachRolePolicy",
      "iam:DetachRolePolicy",
    ]
    resources = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/insolvia-*"]

    condition {
      test     = "ArnEquals"
      variable = "iam:PolicyARN"
      values   = ["arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"]
    }
  }
}

data "aws_caller_identity" "current" {}

resource "aws_iam_role_policy" "github_permissions" {
  name   = "insolvia-deploy"
  role   = aws_iam_role.github_actions.id
  policy = data.aws_iam_policy_document.github_permissions.json
}
