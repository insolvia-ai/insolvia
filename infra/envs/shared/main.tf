# Account-wide, environment-independent resources for Insolvia:
#   • Route53 hosted zone for insolvia.ai
#   • wildcard ACM cert *.insolvia.ai (+ apex SAN), DNS-validated, us-east-1
#   • the SES domain identity for insolvia.ai + all mail DNS (see `email` below)
#
# The GitHub OIDC provider and the insolvia-shared-deploy-role USED to
# live here; they were extracted into infra/envs/ci-trust so the deploy role's
# own policy is never applied by CI (it can't be — see that root's header and
# DenySelfPrivilegeEscalation). Consequence: everything left in `shared` is
# freely CI-applied; there is no human-gated resource here anymore.
#
# Insolvia has its own dedicated AWS account (521762924626).

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

# ── Email: SES identity, DKIM, MAIL FROM, mail DNS (#19, #20) ───
# Lives in `shared` because the SES identity is per-domain, not per-environment:
# staging and prod both send as insolvia.ai.
module "email" {
  source = "../../modules/email"

  aws_region      = var.aws_region
  domain_name     = var.domain_name
  route53_zone_id = aws_route53_zone.main.zone_id

  # Google Workspace domain-ownership token. Route53 permits exactly ONE TXT
  # record set per name, so this cannot be a separate resource — it goes here
  # and the module publishes it in the same set as the apex SPF record. Adding
  # it as its own `aws_route53_record` would silently clobber SPF, which is the
  # trap `additional_apex_txt_records` exists to prevent.
  #
  # Not a secret: verification tokens are public by design and prove only that
  # whoever set them controls this zone.
  additional_apex_txt_records = [
    "google-site-verification=0zLxT_6T4BpPh5oSYJEEUN5EjdGe56DylP9yvnxFaqk",
  ]

  # Google Workspace's DKIM public key, from Admin console → Gmail →
  # Authenticate email. Public by definition — the private half never leaves
  # Google. Currently a 1024-bit key (Google's shorter option); see
  # `var.google_dkim_value` for why 2048 is preferable and why nothing here has
  # to change to switch.
  google_dkim_value = "v=DKIM1;k=rsa;p=MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCh00xKEHBfQhVsuK0hNrNB6jsPiyFwUH1o2xcIUhX885biq4dp5af9qBwzKTjWSw8/DexMf2XnqiyHZyhZ0IP6Ie6ddSgHw9gx8upC4bLrz6MNbJPpqK4app0Bw+ewlVQ9KWfI5riE0Ltc8QGVMGM5CSHbBs8ce2g6ngrS/UgpXwIDAQAB"
}
# ── end email ──────────────────────────────────────────────────

# ── Container repositories ─────────────────────────────────────
# One repo per SERVICE, shared by every environment — deliberately NOT one per
# environment. A shared repo is the whole point of the promotion pipeline:
# prod deploys the exact image digest staging validated, so there has to be
# one place both environments can name it.
#
# Per-env repos would only protect against prod *accidentally* running a
# staging image, and environment isolation does not live here — it lives in
# separate Lambdas, IAM roles, DynamoDB tables, SSM namespaces and Cognito
# pools. An image is the one artifact that is identical across environments by
# construction: every service reads its environment at RUNTIME (the API from
# SSM, the marketing SSR from process.env, the mailer from its Lambda env), so
# nothing environment-specific is ever baked into a layer.
#
# These take `shared` in the environment slot rather than omitting it. They
# genuinely have no environment, and `shared` is what this root's tags have
# always said (Environment = "shared") — so the name now says what the tag says.
# The earlier convention gave them no env segment at all (`insolvia-api`), which
# read fine in isolation and sorted nowhere near `insolvia-prod-api` in a
# console listing. See the insolvia-aws-naming skill § "shared is an
# environment, not an exemption".
#
# The component names match the services they hold images for, including
# `admin-api` rather than `admin` — `admin` is the staff PORTAL, which has no
# container at all (it is a static SPA in an S3 bucket). `jobs` is the one
# repo that is not a service directory of its own: it holds the pipeline
# WORKER image (ADR 0018), built from services/api's Dockerfile `worker`
# target — same source tree as the api image, its own image so 9.6/9.7's
# heavy dependencies land in the worker and never in the request path
# (ADR 0015's rule).
#
# Named `insolvia-*` so the deploy role's ECR grant
# (arn:aws:ecr:...:repository/insolvia-*, infra/envs/ci-trust/main.tf) covers
# them without an IAM change — that grant is human-applied, so a rename OUT of
# that prefix would strand the pipeline.
locals {
  container_repositories = toset(["api", "admin-api", "jobs", "marketing", "mailer"])
}

resource "aws_ecr_repository" "service" {
  for_each = local.container_repositories

  name = "insolvia-shared-${each.key}"
  # MUTABLE is load-bearing: `staging` and `prod` are moving marker tags that CI
  # repoints at each deploy (see the lifecycle policy below).
  image_tag_mutability = "MUTABLE"

  image_scanning_configuration {
    scan_on_push = true
  }

  tags = local.common_tags
}

# Retention is TIME-based, and there is deliberately no catch-all rule.
#
# Two traps this avoids, both of which would delete the image a live Lambda is
# running:
#
#   1. Count-based retention breaks under a shared repo. Staging pushes on
#      every merge to main while prod deploys rarely, so a keep-the-N-newest
#      rule would evict the digest prod is serving within ~N merges. Staging's
#      *rate* has nothing to do with prod's *recency*; a time window is the
#      only bound that tracks what is actually still in use.
#   2. ECR lifecycle rules only ever EXPIRE — they never PROTECT. A "keep the 30
#      newest prod images" rule at priority 1 does not shield those images from
#      a `tagStatus = "any"` rule at priority 3; the catch-all still selects and
#      expires them. The only safe policy is one where no rule can select an
#      in-use image, which means no catch-all at all.
#
# The invariant to preserve when editing this: THE DIGEST ANY LIVE FUNCTION
# POINTS AT MUST REMAIN IN ECR. Rolling back via `update-alias` needs only the
# published Lambda version, but re-deploying an old commit needs its image.
#
# 180 days is sized so a release freeze cannot outlive prod's running image.
# Because layers are shared across builds, real storage sits far below
# (image count x image size).
resource "aws_ecr_lifecycle_policy" "service" {
  for_each = aws_ecr_repository.service

  repository = each.value.name
  policy = jsonencode({
    rules = [
      {
        rulePriority = 1
        description  = "Expire untagged images after a day"
        selection = {
          tagStatus   = "untagged"
          countType   = "sinceImagePushed"
          countUnit   = "days"
          countNumber = 1
        }
        action = { type = "expire" }
      },
      {
        # Matches the `sha-<git-sha>` build tags only. The `staging` and `prod`
        # marker tags are never selected by any rule, so the image an
        # environment is currently running is structurally exempt from
        # expiry — that is the safety property, not an accident of ordering.
        rulePriority = 2
        description  = "Keep every build for 180 days"
        selection = {
          tagStatus     = "tagged"
          tagPrefixList = ["sha-"]
          countType     = "sinceImagePushed"
          countUnit     = "days"
          countNumber   = 180
        }
        action = { type = "expire" }
      },
    ]
  })
}

# ── Inbound mail: Google Workspace, not AWS ────────────────────
# There is deliberately no inbound-mail stack here. hello@ / support@ /
# security@ were once SES receipt rules writing to S3 and a forwarder Lambda
# that re-sent each message to one private mailbox (#21–#25); they are now real
# Google Workspace inboxes, so the whole path — rule set, bucket, Lambda, DLQ,
# alarms, and the forward-to SecureString — was removed.
#
# The apex MX that makes Workspace reachable is owned by `module.email`, and
# only one apex MX set can exist: reinstating SES receiving means taking inbound
# mail away from Workspace. Outbound is untouched — SES still sends as
# no-reply@insolvia.ai.
