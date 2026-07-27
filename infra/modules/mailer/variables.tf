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

# Reserved concurrency for the sender Lambda. null (the default) means "don't
# reserve", and that has to be the default because it is the only value a fresh
# account can accept: a new AWS account's total Lambda concurrency limit is 10,
# AWS refuses any reservation that would drop UnreservedConcurrentExecutions
# below 10, and staging + prod share this one account (521762924626) — so
# reserving even 1 fails until the account-level limit is raised via a Service
# Quotas request.
#
# The reservation is worth restoring once that limit is raised: it caps how
# many sender invocations run at once, which is a deliberate throttle in front
# of SES's send-rate limit. When the quota is raised, set this (e.g. 5) in the
# env that should carry it.
variable "sender_reserved_concurrency" {
  description = "Reserved concurrent executions for the sender Lambda. null = unreserved (required on a fresh account; see comment)."
  type        = number
  default     = null
}

variable "ecr_repository_url" {
  description = "URL of the shared insolvia-mailer repository (created in infra/envs/shared, looked up by the env root). Shared across environments so prod can deploy the exact digest staging validated — see the container-repository note in main.tf."
  type        = string
}

variable "image_tag" {
  description = "Moving per-environment marker tag CI repoints at each deploy ('staging' / 'prod'). Only the first-apply seed: every later deploy sets image_uri by digest, and all three Lambdas move together."
  type        = string
  default     = "latest"
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}
