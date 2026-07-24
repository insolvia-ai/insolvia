variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Infra environment name (staging, prod), used in resource names."
  type        = string
}

variable "domain_name" {
  description = "Fully-qualified hostname the mailer API serves (e.g. staging-mailer-api.insolvia.ai)."
  type        = string
}

variable "hosted_zone_id" {
  description = "Route53 hosted zone ID for insolvia.ai."
  type        = string
}

variable "acm_certificate_arn" {
  description = "ARN of the *.insolvia.ai ACM certificate. Must live in the mailer API's own region — a REGIONAL API Gateway custom domain needs its cert co-located, same constraint as infra/modules/api_service."
  type        = string
}

# The mailer's SigV4 allowlist names exactly one service, insolvia_api, and
# grants execute-api:Invoke to exactly one caller: the API Lambda's own
# execution role. Passed by name (not ARN) because api_service's
# lambda_role_name output is a name, and aws_iam_role_policy needs to attach
# to a role, not just reference an ARN.
variable "caller_role_name" {
  description = "IAM role name of the sole allowed caller of this mailer API — the API Lambda's execution role (module.api_service.lambda_role_name)."
  type        = string
}

variable "sender_address" {
  description = "The From address the insolvia_api service sends as. Both environments share the one insolvia.ai SES domain identity (infra/envs/shared/module.email), so this is deliberately the same in staging and prod."
  type        = string
  default     = "no-reply@insolvia.ai"
}

# Insolvia's MVP sends welcome / email_verification / password_reset mail with
# NO attachments. GuardDuty Malware Protection for S3 is a real monthly cost
# that only earns its keep once attachments exist. The sender only consults
# the GuardDuty scan-status tag for messages whose manifest actually lists
# attachments (services/mailer sender_lambda._attachments iterates
# manifest["attachments"], which is empty for every category this service
# sends today) — so leaving this false does not block or break any current
# send path. Flip to true (both envs, deliberately) when attachments ship.
variable "enable_attachment_scanning" {
  description = "Whether to provision GuardDuty Malware Protection for S3 on the content bucket, plus the EventBridge rules that feed scan results to the feedback Lambda. Defaults to false — see comment above."
  type        = bool
  default     = false
}

# The content bucket's CORS rule is for a browser PUTting an attachment
# directly to a presigned S3 URL. Nothing exercises this path yet (no category
# insolvia_api sends today carries attachments), but S3 CORS requires at least
# one AllowedOrigin, so this stays a required variable rather than an empty
# default — each env passes its own app origin (https://app.insolvia.ai /
# https://staging-app.insolvia.ai).
variable "cors_allowed_origin" {
  description = "Browser origin allowed to PUT to the attachment-upload presigned URL."
  type        = string
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
