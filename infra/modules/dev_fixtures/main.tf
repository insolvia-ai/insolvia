# ── The shared seed-fixture bucket ──────────────────────────────
# ONE bucket for the account, published once, loaded per environment. The
# bytes behind `seeds/fixtures/<version>/` — the sample documents a seeded
# case carries — live here, and every dev stack and staging deploy copies
# them server-side into its own case-documents bucket when it seeds
# (services/admin, entrypoints/seed.py). Nothing here is case data: every
# object is synthetic, reviewable in git under seeds/fixtures/, and the
# loader refuses a target that is not a dev or staging environment.
#
# WHY `envs/shared` OWNS IT, when its name says `dev`. The component says who
# it serves; the root says whose lifecycle it has. `envs/dev` is per machine
# and `dev-aws-destroy.sh` tears it down — a bucket every developer's stack is
# seeded from must be out of reach of any one teardown — and staging seeds
# from it too, so it is neither environment's. Account-level, CI-applied,
# like the container repositories beside it.
#
# WHY A BUCKET AT ALL, when the objects are also in git. Two reasons, and the
# second is the one that will matter: a server-side `copy_object` puts a
# fixture into an environment in milliseconds without the bytes touching the
# machine (or the CI runner) doing the seeding; and the day a fixture holds
# a realistic scanned statement rather than a two-kilobyte PDF, the
# repository is the wrong place for it — the bucket is where the bytes live,
# and git keeps the manifest that names and checksums them.
#
# Its posture, and what each decision protects:
#   • Versioned. It is the only copy of a fixture someone curated, and a
#     re-publish of the same version overwrites keys in place — versioning
#     is the undo.
#   • No force_destroy, and prevent_destroy on top. It outlives every stack
#     that reads it. Renaming it later is an out-of-band empty-then-delete.
#   • All four public-access blocks on, ACLs off, SSE-S3. Nothing here is
#     public: a developer reads it with their own credentials, CI with the
#     seed role's one read grant.
#   • One lifecycle rule, aborting incomplete multipart uploads after seven
#     days. Noncurrent versions are deliberately NOT expired — versioning is
#     the recovery, and a fixture is touched a few times a year.

locals {
  # insolvia-shared-dev-fixtures — the IAM/policy name stem.
  name = "${var.project}-shared-dev-fixtures"

  # The BUCKET takes the region suffix on top, because S3 names are globally
  # unique (insolvia-aws-naming § per-resource-type patterns).
  bucket_name = "${local.name}-${var.aws_region}"
}

resource "aws_s3_bucket" "fixtures" {
  bucket = local.bucket_name

  # Deliberately absent: force_destroy. See the header.
  tags = var.tags

  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_public_access_block" "fixtures" {
  bucket                  = aws_s3_bucket.fixtures.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "fixtures" {
  bucket = aws_s3_bucket.fixtures.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "fixtures" {
  bucket = aws_s3_bucket.fixtures.id
  rule {
    # SSE-S3, not the case key: a fixture is synthetic and the case key's
    # whole point is that CI can never decrypt what it protects. Copying
    # INTO a case bucket re-encrypts under that bucket's default key.
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_versioning" "fixtures" {
  bucket = aws_s3_bucket.fixtures.id
  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "fixtures" {
  bucket = aws_s3_bucket.fixtures.id

  rule {
    id     = "abort-incomplete-multipart"
    status = "Enabled"
    filter {}
    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  depends_on = [aws_s3_bucket_versioning.fixtures]
}

# TLS only. Same statement modules/case_documents makes, for the same reason:
# a fixture is synthetic, but a bucket policy that allows plaintext is a
# habit this account does not have.
data "aws_iam_policy_document" "bucket" {
  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]
    resources = [
      aws_s3_bucket.fixtures.arn,
      "${aws_s3_bucket.fixtures.arn}/*",
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
}

resource "aws_s3_bucket_policy" "fixtures" {
  bucket = aws_s3_bucket.fixtures.id
  policy = data.aws_iam_policy_document.bucket.json

  depends_on = [aws_s3_bucket_public_access_block.fixtures]
}
