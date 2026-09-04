# ── Case documents (issue 8.6) ──────────────────────────────────
# Where the source documents live: credit reports, pay stubs, bank statements.
# The most sensitive bytes this system holds, and the raw material extraction
# (8.7/8.8) reads.
#
# IT TAKES THE CASE KEY RATHER THAN MINTING ONE, which modules/case_store
# reserved for it in as many words: "one key protects one case, documents and
# rows alike". That is not tidiness. The deploy role is denied every data-plane
# KMS verb on `alias/insolvia-*-cases` (ci-trust's DenyCaseDataDecryption), so
# reusing the case key means CI can create this bucket, configure it, and never
# read a byte out of it. A second key would have to earn that deny again, and
# the day it did not, nobody would notice.
#
# The bucket NAME must stay in the `insolvia-*-case-*` family: ci-trust's
# CaseDocumentBuckets grant is scoped to exactly that prefix, and a rename puts
# the bucket outside what CI may manage.

locals {
  # insolvia-<env>-case-documents — the IAM/policy name stem.
  name = "${var.project}-${var.environment}-case-documents"

  # The BUCKET takes a region suffix on top, because S3 bucket names are
  # globally unique across all of AWS (insolvia-aws-naming § per-resource-type
  # patterns). Built from var.aws_region rather than written out, so a region
  # change cannot leave a name claiming us-east-1 behind.
  bucket_name = "${local.name}-${var.aws_region}"

  # What the API needs to broker an upload and a download, and nothing else.
  # No ListBucket: the case's documents are enumerated from the case store,
  # which is the record of what SHOULD be there. Listing the bucket would make
  # an object that exists without a row look like a document, which is exactly
  # what a half-failed upload leaves behind.
  api_object_actions = [
    # Also what HeadObject is authorised by, which is how the confirm endpoint
    # learns an upload actually landed. There is no separate s3:HeadObject.
    "s3:GetObject",
    "s3:PutObject",
    # ONE ACTION, TWO JOBS, AND NO SECOND TAGGING GRANT.
    #
    # The write: S3 evaluates a presigned request against the SIGNER's
    # permissions, so a PutObject carrying `x-amz-tagging` needs this as well
    # as s3:PutObject. Without it every upload is a 403.
    #
    # The clear: confirming an upload has to take the object OUT of the
    # unconfirmed filter below. DeleteObjectTagging is the obvious API for
    # that and is deliberately NOT used — it would need a second tagging
    # action on this role. PutObjectTagging with an empty tag set does the
    # same thing to the object and needs only this grant, which the role
    # cannot avoid holding anyway. The narrower grant is the one that adds
    # nothing: writing an empty tag set is strictly less than writing a
    # populated one, which is already permitted.
    "s3:PutObjectTagging",
    "s3:DeleteObject",
  ]

  # The tag every presigned PUT carries, and the one the lifecycle rule reaps
  # on. Written here rather than inline so the rule and the grant above cannot
  # drift from what services/api signs
  # (adapters/aws/document_blobs.py: UPLOAD_TAG).
  unconfirmed_tag = {
    key   = "upload"
    value = "unconfirmed"
  }
}

resource "aws_s3_bucket" "documents" {
  bucket = local.bucket_name

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

# ── CORS, without which no browser can upload at all ────────────
# NOT a nicety, and not defence in depth: without this resource the feature
# does not work for the only client that ships.
#
# A presigned PUT from the app carries `x-amz-server-side-encryption` (the
# header DenyEncryptionDowngrade below is written against) and `x-amz-tagging`.
# Neither is CORS-safelisted, so the browser will not send the PUT at all until
# it has run an OPTIONS preflight — and S3 answers a preflight on a bucket with
# no CORS configuration with 403. The signature is irrelevant at that point;
# the request never leaves the browser. 100% of uploads fail.
#
# This is the same shape modules/mailer gives its own presigned-PUT bucket.
#
# CORS IS NOT AN ACCESS CONTROL and listing an origin here grants nothing: the
# only way to reach an object is a URL this API signed after checking case
# ownership. What the list does is stop a page on some other origin from
# reading a response in a browser it borrowed a URL into, which is worth having
# and is all it is.
resource "aws_s3_bucket_cors_configuration" "documents" {
  bucket = aws_s3_bucket.documents.id

  cors_rule {
    # PUT for the upload capability; GET so the app can `fetch` a document it
    # was handed a download URL for rather than being forced to navigate to it.
    # No POST (no browser form ever posts here), no DELETE (deletes go through
    # the API, never to S3), no HEAD (nothing asks for one).
    allowed_methods = ["PUT", "GET"]
    allowed_origins = var.cors_allowed_origins

    # EVERY HEADER THE PRESIGN SIGNS, because a signed header the client cannot
    # send is a signature it cannot satisfy.
    #
    #   content-type                  the allowlisted, normalised value
    #   content-length                the exact byte count the size cap binds
    #   x-amz-server-side-encryption  what DenyEncryptionDowngrade requires
    #   x-amz-tagging                 what the unconfirmed-upload rule reaps on
    #
    # `host` is signed too and is deliberately absent: it is a forbidden header
    # name, set by the browser and never offered in a preflight, so naming it
    # here would be noise. `content-length` is in the same family — listed
    # anyway, because the cost is a string and the alternative is the next
    # reader wondering whether it was forgotten.
    allowed_headers = [
      "content-type",
      "content-length",
      "x-amz-server-side-encryption",
      "x-amz-tagging",
    ]

    # The app reads the ETag back to confirm the upload it just made is the
    # object S3 stored. Nothing else is exposed — response headers on a
    # document are this service's business.
    expose_headers = ["etag"]

    # Five minutes. The preflight is one extra round trip per upload session,
    # and caching it longer would mean a CORS change takes longer to take
    # effect than a deploy does.
    max_age_seconds = 300
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
# UNFILTERED expiration rule here: a lifecycle rule that quietly removed a
# filed case's source documents would destroy the evidence behind a signed
# petition, and the retention period a bankruptcy practice actually needs is a
# compliance decision the regulatory register owns, not an engineering default.
#
# The three rules that ARE here all reap bytes no document record points at.
# None of them touches the current version of a confirmed document.
resource "aws_s3_bucket_lifecycle_configuration" "documents" {
  bucket = aws_s3_bucket.documents.id

  # A multipart upload that never completed is not a document, it is a bill.
  # S3 charges for the parts indefinitely and they are invisible in the
  # console's object list.
  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"
    filter {}
    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  # ── The unconfirmed upload ────────────────────────────────────
  # THE CAPABILITY OUTLIVES THE ROW, and this rule is the only thing that can
  # reach what that leaves behind.
  #
  # A presigned PUT is valid for its whole window and its PAYLOAD IS NOT
  # SIGNED, so the same URL can be replayed. Two states come out of that, and
  # neither is reachable from the API:
  #
  #   - POST, then DELETE the document, then PUT through the still-valid URL.
  #     The object lands under a key no row names. `list_for_case` reads the
  #     case store, so it never shows; a second DELETE has nothing to delete;
  #     and the API holds no s3:ListBucket, so nothing can even find it.
  #   - Replay the PUT n times inside the window. Each write is a new version
  #     of an object whose current version is the only one anything names.
  #
  # So every presigned PUT is signed with `upload=unconfirmed`
  # (adapters/aws/document_blobs.py) and this rule expires anything still
  # wearing that tag after a day — comfortably longer than the 15-minute
  # capability that created it, short enough that abandoned bytes are not a
  # standing bill or a standing disclosure.
  #
  # ── THE INVARIANT THAT MAKES THIS RULE SAFE ───────────────────
  #
  #   An object still tagged `upload=unconfirmed` is an object no client ever
  #   completed. Nothing else can leave the tag in place.
  #
  # It holds because of exactly one thing:
  # POST /v1/cases/<case_id>/documents/<document_id>/complete
  # (api/routes/documents.py) HeadObjects the key, clears the tag, and moves
  # the row to `stored`. That endpoint is the ONLY writer of an untagged
  # object, and it runs only after seeing the bytes.
  #
  # REMOVING OR SHORT-CIRCUITING THAT ENDPOINT RE-ARMS THIS RULE AS A DATA
  # LOSS BUG: with nothing clearing the tag, a successfully uploaded document
  # wears it forever and is deleted 24 hours after it arrives, exactly like an
  # orphan — which would contradict the retention posture stated above and do
  # it silently. The two halves are one mechanism. Change either and check the
  # other; the API's grant above carries s3:PutObjectTagging for this reason as
  # much as for the write.
  rule {
    id     = "expire-unconfirmed-uploads"
    status = "Enabled"
    filter {
      tag {
        key   = local.unconfirmed_tag.key
        value = local.unconfirmed_tag.value
      }
    }
    expiration {
      days = 1
    }
  }

  # ── Noncurrent versions ───────────────────────────────────────
  # Versioning is on and, until now, nothing bounded it. A version is created
  # by an overwrite or a delete and is named by nothing afterwards: the API
  # holds no s3:ListBucket and no s3:DeleteObjectVersion, so a noncurrent
  # version could neither be enumerated nor removed by anything in this system.
  # One replayed presigned PUT could park an unbounded number of 50 MiB objects
  # that nobody could see and nobody could delete.
  #
  # THIRTY DAYS, and the number is a window rather than a policy. Versioning
  # exists here for "the case nobody planned — a bug, or a delete that should
  # not have happened", and a wrong delete is noticed when someone goes looking
  # for the document, which in a bankruptcy matter is weeks rather than hours.
  # Thirty days covers that; it is also the ordinary "oops" window for this
  # kind of store, so it is the number a reader can predict.
  #
  # Shorter (7) would make an accidental delete unrecoverable inside a single
  # billing cycle, which defeats the reason versioning is on at all. Longer
  # (90, 365) would only widen the replay ceiling, and the ceiling is what this
  # rule exists to close: the abuse is bounded by a 15-minute capability, so
  # the storage it can strand is bounded by this number times that.
  #
  # It also gives "kept until the case is deleted" a definite end: 30 days
  # after a document is deleted, its bytes are gone rather than noncurrent
  # forever.
  rule {
    id     = "expire-noncurrent-versions"
    status = "Enabled"
    filter {}
    noncurrent_version_expiration {
      noncurrent_days = 30
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
  # hold for a deny, so this means "the header is present AND it is not
  # aws:kms". Silence still gets KMS, from the default above.
  #
  # WHAT THAT DENIES IS NOT ONLY A DOWNGRADE, and the sid undersells it. There
  # are three values S3 accepts, and this refuses two of them:
  #
  #   AES256        SSE-S3. A genuine downgrade — the bytes leave the
  #                 customer-managed key, and ci-trust's alias-scoped deny
  #                 stops meaning anything.
  #   aws:kms:dsse  DSSE-KMS, dual-layer. STRONGER than what this bucket asks
  #                 for, and still refused.
  #
  # Refusing the stronger one is deliberate rather than an oversight. Bucket
  # default encryption applies only when a request names no algorithm at all,
  # so `aws:kms:dsse` with no key id does not inherit this bucket's key — S3
  # encrypts under the AWS-managed `aws/s3` key instead, and a case document
  # lands outside `alias/insolvia-*-cases` while looking like an upgrade in the
  # request. The statement below closes the same hole for an explicit key id;
  # this one closes it for the algorithm. Nothing in this system asks for DSSE,
  # and one encryption mode is one thing to reason about.
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

  # THE STATEMENT ABOVE FENCES THE ALGORITHM AND NOT THE KEY, which is half a
  # fence. `aws:kms` satisfies it, and `aws:kms` plus an
  # `x-amz-server-side-encryption-aws-kms-key-id` naming some OTHER key stores
  # a case document under a key that is not the case key — outside
  # `alias/insolvia-*-cases`, which is the exact thing ci-trust's
  # DenyCaseDataDecryption is scoped against. The deny that keeps the deploy
  # role from reading case data would simply not apply to that object.
  #
  # NOT REACHABLE THROUGH A PRESIGNED URL, and saying so is the point of this
  # comment: adapters/aws/document_blobs.py deliberately names no key, so the
  # header is not in the signature and a client adding it invalidates the
  # request before S3 evaluates any policy. This is hardening for the other
  # principals the `"*"` statements above cover — every role in the account
  # that S3 will evaluate this policy for, including a future one nobody has
  # written yet. That is the whole reason it is a bucket policy and not a note
  # in the API's IAM grant.
  #
  # Same `Null` pairing, for the same reason and it is just as load-bearing:
  # an ordinary upload names no key id, `StringNotEquals` is TRUE when the key
  # is absent, and without the pairing this would deny every upload the
  # statement above just allowed.
  statement {
    sid       = "DenyForeignEncryptionKey"
    effect    = "Deny"
    actions   = ["s3:PutObject"]
    resources = ["${aws_s3_bucket.documents.arn}/*"]
    principals {
      type        = "*"
      identifiers = ["*"]
    }
    condition {
      test     = "StringNotEquals"
      variable = "s3:x-amz-server-side-encryption-aws-kms-key-id"
      values   = [var.kms_key_arn]
    }
    condition {
      test     = "Null"
      variable = "s3:x-amz-server-side-encryption-aws-kms-key-id"
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
# Attached from inside this module onto the role var.api_role_name names — the
# same shape modules/case_store uses; its comment owns why the name is used
# directly (neither a cross-boundary resource reference nor a plan-time data
# lookup).
resource "aws_iam_role_policy" "api_document_access" {
  count = var.api_role_name == null ? 0 : 1

  name = "${local.name}-access"
  role = var.api_role_name

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

# ── The second application principal: the pipeline worker ───────
# Issue #96: packet assembly runs in the worker Lambda and WRITES its
# assembled packet into this bucket (cases/<case_id>/packets/<packet_id>) —
# a direct PutObject under the worker's own role, not a presigned capability,
# because the worker holds the bytes itself. Same amendment shape as
# modules/case_store's worker grant: a second, narrower principal with a
# grant of its own, attached from this module's side via the role NAME.
#
# Issues 8.7/8.8 widened it with READ: the extraction worker sends a source
# document's own bytes to the model API (insolvia_core.ports.
# DocumentBlobStore.get_bytes), so GetObject covers cases/* — which includes
# the packets the worker itself wrote, harmless because it wrote them. The
# API's read path stays the brokered presigned URL; this grant never reaches
# the request-path Lambda.
#
# Still narrower than the API's on purpose: PutObject stays on the packets
# prefix alone (the worker never writes a source document); no
# PutObjectTagging — a worker write and its record land in the same
# operation (core/ports.PacketStore), so nothing it stores is ever
# "unconfirmed"; no ListBucket, the API grant's own argument — which is also
# why a missing key answers the worker 403, the absent-vs-denied fold
# get_bytes documents.
resource "aws_iam_role_policy" "worker_document_access" {
  count = var.worker_role_name == null ? 0 : 1

  name = "${local.name}-worker-write"
  role = var.worker_role_name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "CasePacketObjectsWrite"
        Effect = "Allow"
        Action = ["s3:PutObject"]
        # The packets prefix only — the worker cannot WRITE uploaded source
        # documents, whose keys sit directly under cases/<case_id>/.
        Resource = "${aws_s3_bucket.documents.arn}/cases/*/packets/*"
      },
      {
        Sid    = "CaseDocumentObjectsRead"
        Effect = "Allow"
        Action = ["s3:GetObject"]
        # Source documents AND packets — see the header for why the wider
        # read is acceptable where the wider write is not.
        Resource = "${aws_s3_bucket.documents.arn}/cases/*"
      },
      {
        # GenerateDataKey is what an SSE-KMS PutObject needs, Decrypt what
        # an SSE-KMS GetObject needs; both fenced to S3 as the calling
        # service exactly as the API's grant is, so this can never become a
        # direct Decrypt of a case row.
        Sid      = "CasePacketKeyUse"
        Effect   = "Allow"
        Action   = ["kms:GenerateDataKey", "kms:Decrypt", "kms:DescribeKey"]
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
