# ── Case documents (issue 8.6) ──────────────────────────────────
# Where the source documents live: credit reports, pay stubs, bank statements.
# The most sensitive bytes this system holds, and the raw material extraction
# (8.7/8.8) reads.
#
# IT TAKES THE CASE KEY RATHER THAN MINTING ONE, which modules/case_store
# reserved for it in as many words: "one key protects one case, documents and
# rows alike". That is not tidiness. The deploy role is denied every data-plane
# KMS verb on `alias/insolvia-cases-*` (ci-trust's DenyCaseDataDecryption), so
# reusing the case key means CI can create this bucket, configure it, and never
# read a byte out of it. A second key would have to earn that deny again, and
# the day it did not, nobody would notice.
#
# The bucket NAME must stay in the `insolvia-case-*` family: ci-trust's
# CaseDocumentBuckets grant is scoped to exactly that prefix, and a rename puts
# the bucket outside what CI may manage.

locals {
  # insolvia-case-documents-<env>
  name = "${var.project}-case-documents-${var.environment}"

  # What the API needs to broker an upload and a download, and nothing else.
  # No ListBucket: the case's documents are enumerated from the case store,
  # which is the record of what SHOULD be there. Listing the bucket would make
  # an object that exists without a row look like a document, which is exactly
  # what a half-failed upload leaves behind.
  api_object_actions = [
    "s3:GetObject",
    "s3:PutObject",
    "s3:DeleteObject",
  ]
}

data "aws_iam_role" "api" {
  count = var.api_role_name == null ? 0 : 1
  name  = var.api_role_name
}

resource "aws_s3_bucket" "documents" {
  bucket = local.name

  # Prod on, staging off — the same split modules/case_store makes, and for the
  # same reason: staging holds synthetic documents and stays disposable, prod
  # holds a client's tax returns.
  force_destroy = var.force_destroy

  tags = var.tags
}

# ── Nothing about this bucket is public ─────────────────────────
# All four, explicitly. The account-level block is not a substitute: it can be
# turned off in one click by someone fixing an unrelated bucket, and this is
# the bucket where that mistake is unrecoverable.
resource "aws_s3_bucket_public_access_block" "documents" {
  bucket                  = aws_s3_bucket.documents.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "documents" {
  bucket = aws_s3_bucket.documents.id
  rule {
    # ACLs disabled outright. Every object is owned by this account, so an ACL
    # cannot be the thing that grants access — the bucket policy and IAM are.
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "documents" {
  bucket = aws_s3_bucket.documents.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm     = "aws:kms"
      kms_master_key_id = var.kms_key_arn
    }
    # S3 Bucket Keys cut KMS request costs by roughly 99% on a bucket like
    # this one, where every object shares a key. It changes nothing about who
    # can decrypt: the data key is still derived from the CMK and the same IAM
    # and key policies apply.
    bucket_key_enabled = true
  }
}

# ── Versioning, because a document is evidence ──────────────────
# An overwritten pay stub is not a corrected pay stub; it is a lost one. The
# API never overwrites (each upload is its own object id), so this exists for
# the case nobody planned — a bug, or a delete that should not have happened.
resource "aws_s3_bucket_versioning" "documents" {
  bucket = aws_s3_bucket.documents.id
  versioning_configuration {
    status = "Enabled"
  }
}

# ── Retention, stated rather than left to be discovered ─────────
# THE POSTURE IS "KEPT UNTIL THE CASE IS DELETED", and issue 8.6 asks for it to
# be written down even if that is the answer. So there is deliberately NO
# expiration rule here: a lifecycle rule that quietly removed a filed case's
# source documents would destroy the evidence behind a signed petition, and the
# retention period a bankruptcy practice actually needs is a compliance
# decision the regulatory register owns, not an engineering default.
#
# What IS here is the one rule with no data in it: a multipart upload that
# never completed is not a document, it is a bill. S3 charges for the parts
# indefinitely and they are invisible in the console's object list.
resource "aws_s3_bucket_lifecycle_configuration" "documents" {
  bucket = aws_s3_bucket.documents.id

  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"
    filter {}
    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  # Ordering: S3 rejects a lifecycle configuration on a bucket whose versioning
  # is still settling.
  depends_on = [aws_s3_bucket_versioning.documents]
}

# ── TLS only ────────────────────────────────────────────────────
# A presigned URL is a bearer token in a query string. Over plain HTTP it is
# readable by anything on the path, and the object it names is a client's
# financial history — so the bucket refuses the request rather than trusting
# every caller to have used https.
data "aws_iam_policy_document" "bucket" {
  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]
    resources = [
      aws_s3_bucket.documents.arn,
      "${aws_s3_bucket.documents.arn}/*",
    ]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }

  # A request that explicitly asks for AES256 would store a case document
  # outside the customer-managed key entirely, so it is refused here rather
  # than left to the bucket default.
  #
  # THE `Null` CONDITION IS LOAD-BEARING, and leaving it out breaks every
  # ordinary upload — verified against a real bucket rather than reasoned
  # about. `StringNotEquals` is TRUE when the key is absent, and a request that
  # says nothing about encryption carries no `x-amz-server-side-encryption`
  # header at all: the deny fired on exactly the requests that were relying on
  # the bucket's default, which is to say all of them. Both conditions must
  # hold for a deny, so this now means "the header is present AND it is not
  # aws:kms" — an explicit downgrade, and nothing else. Silence still gets KMS,
  # from the default above.
  statement {
    sid       = "DenyEncryptionDowngrade"
    effect    = "Deny"
    actions   = ["s3:PutObject"]
    resources = ["${aws_s3_bucket.documents.arn}/*"]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    condition {
      test     = "StringNotEquals"
      variable = "s3:x-amz-server-side-encryption"
      values   = ["aws:kms"]
    }
    condition {
      test     = "Null"
      variable = "s3:x-amz-server-side-encryption"
      values   = ["false"]
    }
  }
}

resource "aws_s3_bucket_policy" "documents" {
  bucket = aws_s3_bucket.documents.id
  policy = data.aws_iam_policy_document.bucket.json

  # The public access block must land first, or a bucket policy naming a "*"
  # principal is briefly evaluated as a public policy and rejected.
  depends_on = [aws_s3_bucket_public_access_block.documents]
}

# ── The one application principal ───────────────────────────────
# Attached from inside this module onto the role looked up above — the same
# shape modules/case_store uses, and for the same reason: a resource reference
# across the boundary would make api_service depend on this module.
resource "aws_iam_role_policy" "api_document_access" {
  count = var.api_role_name == null ? 0 : 1

  name = "access-${local.name}"
  role = data.aws_iam_role.api[0].id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "CaseDocumentObjects"
        Effect   = "Allow"
        Action   = local.api_object_actions
        Resource = "${aws_s3_bucket.documents.arn}/*"
      },
      {
        # The bucket-level verb the API genuinely needs, and only it. Present
        # so a presigned PUT can be signed against a bucket the role can
        # resolve; deliberately NOT s3:ListBucket — see the note on
        # api_object_actions.
        Sid      = "CaseDocumentBucket"
        Effect   = "Allow"
        Action   = ["s3:GetBucketLocation"]
        Resource = aws_s3_bucket.documents.arn
      },
      {
        # Fenced to S3 as the calling service, so this grant can never become a
        # direct Decrypt of a case ROW. modules/case_store fences its own the
        # same way, in the opposite direction.
        Sid    = "CaseDocumentKeyUse"
        Effect = "Allow"
        Action = [
          "kms:Decrypt",
          "kms:GenerateDataKey",
          "kms:DescribeKey",
        ]
        Resource = var.kms_key_arn
        Condition = {
          StringEquals = {
            "kms:ViaService" = "s3.${var.aws_region}.amazonaws.com"
          }
        }
      },
    ]
  })
}
