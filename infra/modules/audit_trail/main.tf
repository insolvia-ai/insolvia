# ── Case-data audit trail (issue 8.2) ───────────────────────────
# CloudTrail data events for the stores holding GLBA-scope data, and the
# bucket they land in. Business plan §10 commits to audit logging alongside
# encryption and least privilege; this is that commitment.
#
# WHAT THIS CAN AND CANNOT PROVE. Per ADR 0001 the API Lambda's role is the
# only application principal, so every data event here names
# `insolvia-api-<env>-role` and never the signed-in user behind it. That makes
# this evidence about ADMINISTRATIVE access — the "no human read paths in
# prod" claim — and about the shape and volume of application access. "Which
# user read this SSN" is a different question, and it has to be answered by an
# application-level log the API writes itself. Do not let this trail's
# existence stand in for that one.
#
# Data events are billed per event, unlike management events. The selector
# below is scoped to named resources rather than "all DynamoDB tables" so the
# bill tracks case access rather than everything in the account.

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

locals {
  # insolvia-audit-<env>
  name = "${var.project}-audit-${var.environment}"

  # Bucket and trail names must both match the insolvia-audit-* prefixes the
  # deploy role is granted in infra/envs/ci-trust. Renaming this stem without
  # renaming those is a human-applied round trip away from a broken deploy.
  bucket_name = local.name

  # Constructed rather than read off aws_cloudtrail.audit.arn, and that is the
  # point: the bucket policy must name the trail, and the trail must name the
  # bucket. Referencing the resource would be a cycle; the name is ours and
  # the ARN is deterministic from it.
  trail_arn = "arn:aws:cloudtrail:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:trail/${local.name}"
}

# ── The key ─────────────────────────────────────────────────────
# A SEPARATE key from the case store's, and not an oversight. Two reasons,
# the first of which is load-bearing:
#
#   1. It cannot be the case key. ci-trust's DenyCaseDataDecryption denies the
#      deploy role kms:GenerateDataKey on anything aliased alias/insolvia-cases-*
#      — verified with simulate-principal-policy — so a trail pointed at the
#      case key fails at CreateTrail, not at review.
#   2. Separating them is the point anyway. The deploy role holds no kms:Decrypt
#      for audit keys in any statement, so it can create this trail, write to
#      it, and never read what it recorded. An audit log its own pipeline can
#      read back is weaker evidence.
data "aws_iam_policy_document" "key" {
  # Same root delegation, and the same reasoning, as modules/case_store's key
  # — a key policy naming no principal able to change it is unrecoverable.
  statement {
    sid    = "EnableIAMPolicies"
    effect = "Allow"
    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:root"]
    }
    actions   = ["kms:*"]
    resources = ["*"]
  }

  # CloudTrail encrypts each log file under a data key it asks for itself, as
  # the service principal — this is what makes SSE-KMS on a trail work at all.
  # Fenced by the encryption context AWS attaches, so the grant cannot be
  # reused to wrap anything that is not one of this account's trails.
  statement {
    sid    = "CloudTrailEncryptsLogs"
    effect = "Allow"
    principals {
      type        = "Service"
      identifiers = ["cloudtrail.amazonaws.com"]
    }
    actions   = ["kms:GenerateDataKey*"]
    resources = ["*"]

    condition {
      test     = "StringLike"
      variable = "kms:EncryptionContext:aws:cloudtrail:arn"
      values   = ["arn:aws:cloudtrail:*:${data.aws_caller_identity.current.account_id}:trail/*"]
    }
  }

  statement {
    sid    = "CloudTrailDescribesKey"
    effect = "Allow"
    principals {
      type        = "Service"
      identifiers = ["cloudtrail.amazonaws.com"]
    }
    actions   = ["kms:DescribeKey"]
    resources = ["*"]
  }
}

resource "aws_kms_key" "audit" {
  description             = "Insolvia case-data audit trail (${var.environment}). Separate from the case key on purpose — see modules/audit_trail."
  enable_key_rotation     = true
  deletion_window_in_days = var.key_deletion_window_in_days
  policy                  = data.aws_iam_policy_document.key.json
  tags                    = var.tags
}

resource "aws_kms_alias" "audit" {
  name          = "alias/${local.name}"
  target_key_id = aws_kms_key.audit.key_id
}

# ── The bucket ──────────────────────────────────────────────────
resource "aws_s3_bucket" "audit" {
  bucket = local.bucket_name
  tags   = var.tags
}

# Versioning is not tidiness here: ci-trust's DenyAuditLogErasure denies the
# deploy role DeleteObject AND DeleteObjectVersion, and versioning is what
# makes the second half meaningful — without it an overwrite is a silent
# deletion that never calls a Delete API.
resource "aws_s3_bucket_versioning" "audit" {
  bucket = aws_s3_bucket.audit.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "audit" {
  bucket                  = aws_s3_bucket.audit.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "audit" {
  bucket = aws_s3_bucket.audit.id
  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = aws_kms_key.audit.arn
    }
    # Log files land under one prefix; S3 Bucket Keys cut KMS request cost
    # substantially for that access pattern and change nothing semantically.
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "audit" {
  bucket = aws_s3_bucket.audit.id

  rule {
    id     = "retain-then-expire"
    status = "Enabled"

    filter {}

    expiration {
      days = var.retention_days
    }

    # Versioning is on, so the non-current copies need their own rule or the
    # bucket grows without bound behind the scenes.
    noncurrent_version_expiration {
      noncurrent_days = var.retention_days
    }
  }
}

# CloudTrail writes as a service principal, so the bucket has to say so. Both
# statements are required and neither is optional: CloudTrail checks the ACL
# before its first write and refuses to create the trail without it.
data "aws_iam_policy_document" "bucket" {
  statement {
    sid    = "CloudTrailAclCheck"
    effect = "Allow"
    principals {
      type        = "Service"
      identifiers = ["cloudtrail.amazonaws.com"]
    }
    actions   = ["s3:GetBucketAcl"]
    resources = [aws_s3_bucket.audit.arn]

    condition {
      test     = "StringEquals"
      variable = "aws:SourceArn"
      values   = [local.trail_arn]
    }
  }

  statement {
    sid    = "CloudTrailWrite"
    effect = "Allow"
    principals {
      type        = "Service"
      identifiers = ["cloudtrail.amazonaws.com"]
    }
    actions   = ["s3:PutObject"]
    resources = ["${aws_s3_bucket.audit.arn}/AWSLogs/${data.aws_caller_identity.current.account_id}/*"]

    condition {
      test     = "StringEquals"
      variable = "s3:x-amz-acl"
      values   = ["bucket-owner-full-control"]
    }
    condition {
      test     = "StringEquals"
      variable = "aws:SourceArn"
      values   = [local.trail_arn]
    }
  }

  # Belt and braces on the confidentiality side: nothing may read or write
  # these objects over plain HTTP, matching the case table's SecureTransport
  # condition.
  statement {
    sid    = "DenyInsecureTransport"
    effect = "Deny"
    principals {
      type        = "AWS"
      identifiers = ["*"]
    }
    actions   = ["s3:*"]
    resources = [aws_s3_bucket.audit.arn, "${aws_s3_bucket.audit.arn}/*"]

    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "audit" {
  bucket = aws_s3_bucket.audit.id
  policy = data.aws_iam_policy_document.bucket.json

  # The policy names the trail ARN and the trail names the bucket. Constructing
  # the ARN in locals rather than referencing aws_cloudtrail.audit.arn is what
  # breaks that cycle — the name is ours and deterministic.
  depends_on = [aws_s3_bucket_public_access_block.audit]
}

# ── The trail ───────────────────────────────────────────────────
resource "aws_cloudtrail" "audit" {
  name           = local.name
  s3_bucket_name = aws_s3_bucket.audit.id
  kms_key_id     = aws_kms_key.audit.arn

  # Single-region: every Insolvia resource is us-east-1 (infra/CLAUDE.md), so
  # a multi-region trail would bill for empty regions. Global service events
  # are off for the same reason management events are — this trail exists to
  # record case-data access, and the account has no other trail competing to
  # record them.
  is_multi_region_trail         = false
  include_global_service_events = false
  enable_log_file_validation    = true
  enable_logging                = true

  # Management events are deliberately excluded. They would double the volume
  # to say things the Terraform diff already says, and this trail's job is the
  # data plane.
  advanced_event_selector {
    name = "Case data access"

    field_selector {
      field  = "eventCategory"
      equals = ["Data"]
    }
    field_selector {
      field  = "resources.type"
      equals = ["AWS::DynamoDB::Table"]
    }
    field_selector {
      field       = "resources.ARN"
      starts_with = var.data_resource_arns
    }
  }

  tags = var.tags

  depends_on = [aws_s3_bucket_policy.audit]
}
