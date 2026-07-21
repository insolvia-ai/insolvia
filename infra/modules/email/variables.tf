variable "aws_region" {
  description = <<-EOT
    AWS region hosting the SES identity. Determines both the MAIL FROM feedback
    endpoint (feedback-smtp.<region>.amazonses.com) and the inbound receiving
    endpoint (inbound-smtp.<region>.amazonaws.com), so it must be a region where
    SES receiving is available.
  EOT
  type        = string
  default     = "us-east-1"
}

variable "domain_name" {
  description = "Apex domain to verify as an SES identity (e.g. insolvia.ai)."
  type        = string
}

variable "route53_zone_id" {
  description = "Route53 hosted zone ID containing the SES and mail DNS records."
  type        = string
}

variable "additional_apex_txt_records" {
  description = <<-EOT
    Extra TXT values to publish at the apex alongside the SES SPF record. Route53
    permits only one TXT record set per name, so anything else needing an apex TXT
    (domain-ownership verification tokens, for example) must be added here rather
    than as a separate resource, which would clobber the SPF record.
  EOT
  type        = list(string)
  default     = []
}
