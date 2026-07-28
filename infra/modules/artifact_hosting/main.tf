# Artifact hosting for the unsigned desktop builds (issue 4.10): a private S3
# origin behind CloudFront (OAC), TLS via the shared wildcard ACM cert, and a
# Route53 alias. Compute-free, exactly like `web_hosting`.
#
# WHY A SEPARATE MODULE AND NOT A FLAG ON `web_hosting`:
# `web_hosting` is a *Flutter-web SPA* host, and three of its defining choices
# are wrong here in ways a boolean cannot paper over:
#
#   • It rewrites 403/404 to `/index.html` with a 200 so go_router deep links
#     work. On a download host that turns a typo'd artifact URL into a
#     200-with-HTML — the single worst failure mode for `curl -O`, which would
#     happily write an HTML error page to `Insolvia.dmg`. A 404 must stay a 404.
#   • It sets `default_root_object = "index.html"`. There is no index here, by
#     intent: recipients get exact URLs (D8 — the host is unlinked).
#   • Its content is a whole-bucket sync of mutable files. These are immutable
#     per-release binaries whose freshness rules, download semantics, and
#     indexability posture are all different.
#
# Parameterising all three would leave `web_hosting` a two-personality module
# where every future reader has to work out which half applies. Two
# single-concern modules that happen to share an S3+CloudFront+OAC skeleton is
# the cheaper thing to maintain — the same call `marketing_site` made.

locals {
  bucket_name = "${var.project}-download-${var.environment}"
}

# ── Origin bucket (private) ─────────────────────────────────────
resource "aws_s3_bucket" "artifacts" {
  bucket = local.bucket_name
  tags   = var.tags
}

resource "aws_s3_bucket_public_access_block" "artifacts" {
  bucket                  = aws_s3_bucket.artifacts.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "artifacts" {
  bucket = aws_s3_bucket.artifacts.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
  }
}

# A `.dmg` is well over the 8 MB threshold at which the AWS CLI switches to a
# multipart upload, and a CI job cancelled mid-`s3 cp` leaves the parts behind
# as billable storage that nothing ever lists or reclaims. This is the one
# lifecycle rule the bucket genuinely needs — the artifacts themselves are
# deliberately NOT expired, because a URL handed to a firm should not rot on a
# timer.
resource "aws_s3_bucket_lifecycle_configuration" "artifacts" {
  bucket = aws_s3_bucket.artifacts.id

  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"
    filter {}
    abort_incomplete_multipart_upload { days_after_initiation = 1 }
  }
}

# ── CloudFront access to the bucket (OAC, no public access) ─────
resource "aws_cloudfront_origin_access_control" "artifacts" {
  name                              = "${local.bucket_name}-oac"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

# Response headers the HOST guarantees, independent of how any object was
# uploaded. This matters because the upload step lives in a different workflow
# (and a different pull request): a header that must hold for every artifact
# belongs to the distribution, not to whoever last edited an `aws s3 cp` line.
#
#   • X-Robots-Tag — the download host must not be indexed, for the same reason
#     staging marketing is not (D2): a public host that starts ranking competes
#     with insolvia.ai for its own name, and here it would rank an UNSIGNED
#     installer. `robots.txt` (below) asks crawlers not to fetch; this tells the
#     ones that fetched anyway not to index. Both, because robots.txt is a
#     request and the header is an instruction.
#   • Content-Disposition — a `.dmg`/`.exe` must land in the Downloads folder,
#     never render. `override = false` on purpose: this is a FLOOR, so an upload
#     that sets `attachment; filename="Insolvia-1.2.3.dmg"` still wins and gets
#     a nicer saved filename. Without it, the fallback filename is the last path
#     segment of the URL, which is already correct.
#   • nosniff — content-type sniffing on attacker-influenced bytes is exactly
#     the thing you do not want on a host whose entire job is serving binaries.
resource "aws_cloudfront_response_headers_policy" "artifacts" {
  name    = "${local.bucket_name}-headers"
  comment = "${var.project} ${var.environment} downloads: force download, never index"

  custom_headers_config {
    items {
      header   = "X-Robots-Tag"
      value    = "noindex, nofollow"
      override = true
    }

    items {
      header   = "Content-Disposition"
      value    = "attachment"
      override = false
    }
  }

  security_headers_config {
    content_type_options {
      override = true
    }

    strict_transport_security {
      access_control_max_age_sec = 31536000
      include_subdomains         = true
      override                   = true
    }
  }
}

# `robots.txt` needs the noindex header but must NOT be served as an
# attachment, so it gets its own policy rather than an exemption from the one
# above — a response headers policy applies wholesale to a behavior.
resource "aws_cloudfront_response_headers_policy" "robots" {
  name    = "${local.bucket_name}-robots-headers"
  comment = "${var.project} ${var.environment} downloads: robots.txt (no attachment disposition)"

  custom_headers_config {
    items {
      header   = "X-Robots-Tag"
      value    = "noindex, nofollow"
      override = true
    }
  }

  security_headers_config {
    content_type_options {
      override = true
    }
  }
}

resource "aws_cloudfront_distribution" "artifacts" {
  enabled     = true
  aliases     = [var.domain_name]
  price_class = "PriceClass_100"
  comment     = "${var.project} ${var.environment} downloads"
  tags        = var.tags

  # No `default_root_object`, deliberately. There is no index page and there
  # must not be one: the host is unlinked (D8) and recipients are handed exact
  # URLs. An S3 REST origin never produces a directory listing either, so `/`
  # and any prefix return a plain 403 from S3 and CloudFront passes it through
  # unchanged — no bucket contents are ever enumerable.

  origin {
    origin_id                = "s3-${local.bucket_name}"
    domain_name              = aws_s3_bucket.artifacts.bucket_regional_domain_name
    origin_access_control_id = aws_cloudfront_origin_access_control.artifacts.id
  }

  default_cache_behavior {
    target_origin_id       = "s3-${local.bucket_name}"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD", "OPTIONS"]
    cached_methods         = ["GET", "HEAD"]

    # Inert for the artifacts themselves — CloudFront only compresses a fixed
    # list of text content types, and never a `.dmg`/`.exe` (already
    # compressed archives). Left on so it applies to anything textual served
    # alongside them.
    compress = true

    # AWS-managed "CachingOptimized". Its DEFAULT ttl is 24 h, but the origin's
    # `Cache-Control` wins wherever one is present — which is the whole point,
    # and why no custom short-TTL policy is needed here. The upload step sets
    # the freshness rule per object, because the two kinds of object on this
    # host have opposite requirements:
    #
    #   • Versioned release paths (`/1.2.3/Insolvia.dmg`) are immutable — upload
    #     them with `--cache-control "public, max-age=31536000, immutable"` and
    #     they are cached at the edge effectively forever, with no invalidation.
    #   • Any moving pointer (`/latest/...`) must NOT be, or a recipient who
    #     followed the link yesterday gets yesterday's build — upload those with
    #     a short max-age (or `no-cache`) and re-point them freely.
    #
    # An artifact overwritten in place at the same key is the one case that
    # still needs `create-invalidation`; prefer a new versioned key instead.
    cache_policy_id            = data.aws_cloudfront_cache_policy.optimized.id
    response_headers_policy_id = aws_cloudfront_response_headers_policy.artifacts.id
  }

  ordered_cache_behavior {
    path_pattern           = "/robots.txt"
    target_origin_id       = "s3-${local.bucket_name}"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    cache_policy_id            = data.aws_cloudfront_cache_policy.optimized.id
    response_headers_policy_id = aws_cloudfront_response_headers_policy.robots.id
  }

  # NO `custom_error_response` blocks — the opposite of `web_hosting`, and the
  # single most important difference. A 404 here must reach the client as a
  # 404 so `curl -fO` fails loudly instead of saving an HTML page under a
  # `.dmg` name.

  viewer_certificate {
    acm_certificate_arn      = var.acm_certificate_arn
    ssl_support_method       = "sni-only"
    minimum_protocol_version = "TLSv1.2_2021"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }
}

data "aws_cloudfront_cache_policy" "optimized" {
  name = "Managed-CachingOptimized"
}

# ── robots.txt ──────────────────────────────────────────────────
# Terraform-managed rather than part of the upload step, because it is a
# property of the HOST and not of any build: it must be correct from the first
# apply, including in the window before CI has ever uploaded an artifact, and
# it must not disappear if the desktop build jobs are ever skipped.
resource "aws_s3_object" "robots" {
  bucket        = aws_s3_bucket.artifacts.id
  key           = "robots.txt"
  content       = "User-agent: *\nDisallow: /\n"
  content_type  = "text/plain"
  cache_control = "public, max-age=300"

  # Content-addressed so an edit to the string above actually re-uploads.
  etag = md5("User-agent: *\nDisallow: /\n")

  tags = var.tags
}

# ── Bucket policy: only this distribution may read ──────────────
data "aws_iam_policy_document" "artifacts" {
  statement {
    sid       = "AllowCloudFrontOAC"
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.artifacts.arn}/*"]

    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.artifacts.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "artifacts" {
  bucket = aws_s3_bucket.artifacts.id
  policy = data.aws_iam_policy_document.artifacts.json
}

# ── DNS ─────────────────────────────────────────────────────────
resource "aws_route53_record" "artifacts" {
  zone_id = var.hosted_zone_id
  name    = var.domain_name
  type    = "A"

  alias {
    name                   = aws_cloudfront_distribution.artifacts.domain_name
    zone_id                = aws_cloudfront_distribution.artifacts.hosted_zone_id
    evaluate_target_health = false
  }
}
