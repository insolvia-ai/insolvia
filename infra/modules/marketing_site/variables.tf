variable "project" {
  description = "Project slug used in resource names (insolvia)."
  type        = string
}

variable "environment" {
  description = "Environment suffix for resource names (staging | prod)."
  type        = string
}

variable "www_domain" {
  description = "Canonical host the site serves (e.g. www.insolvia.ai)."
  type        = string
}

variable "apex_domain" {
  description = <<-EOT
    Apex host that 301-redirects to www (e.g. insolvia.ai). OPTIONAL: leave
    null on any environment that does not own the apex. There is exactly one
    apex per zone and prod owns it, so staging must pass null — otherwise both
    environments would claim the same CloudFront alias and the same Route53
    records, and the second apply would fail on the alias conflict.

    Null means: no apex alias on the distribution, no apex A/AAAA records, and
    the viewer-request function skips its redirect branch entirely.
  EOT
  type        = string
  default     = null
}

variable "hosted_zone_id" {
  description = "Route53 hosted zone ID for the domain."
  type        = string
}

variable "acm_certificate_arn" {
  description = <<-EOT
    us-east-1 ACM cert covering www — via the `*.insolvia.ai` wildcard — and,
    when apex_domain is set, the apex too (via the cert's SAN).
  EOT
  type        = string
}

variable "api_base_url" {
  description = <<-EOT
    Origin of the Insolvia API the SSR waitlist action POSTs to
    (e.g. https://api.insolvia.ai). Set on the Lambda as
    INSOLVIA_API_BASE_URL — the switch that makes the waitlist real
    (unset means "log instead of submit"). See docs/adr/0001.
  EOT
  type        = string
}

variable "image_tag" {
  description = <<-EOT
    ECR image tag the SSR Lambda is created from. Only consulted at creation:
    after that the deploy workflow owns the running image
    (lifecycle ignore_changes on image_uri).
  EOT
  type        = string
  default     = "latest"
}

variable "site_enabled" {
  description = <<-EOT
    Whether the CloudFront distribution serves traffic. Set false to take the
    site offline without destroying anything: the Lambda, S3 assets, ECR
    images, DNS records and cert all stay exactly as they are, and CloudFront
    simply stops serving (viewers get a CloudFront 403). Flip back to true and
    apply to bring the site back — no rebuild, no ECR bootstrap.
  EOT
  type        = bool
  default     = true
}

variable "tags" {
  description = "Tags applied to all resources."
  type        = map(string)
  default     = {}
}
