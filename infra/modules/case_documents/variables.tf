variable "project" {
  description = "Name prefix. The bucket must stay in the insolvia-*-case-* family — ci-trust's CaseDocumentBuckets grant is scoped to exactly that prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (dev-<id>, staging, prod)."
  type        = string
}

variable "aws_region" {
  description = "Region, used only to fence the API's KMS grant to S3 as the calling service."
  type        = string
}

variable "kms_key_arn" {
  description = "The CASE key, from modules/case_store. Never a key of this module's own — one key protects one case, and the deploy role's data-plane deny is written against that key's alias."
  type        = string
}

# S3 CORS requires at least one AllowedOrigin, so this is required rather than
# defaulted to an empty list: a bucket with an empty CORS configuration behaves
# exactly like a bucket with none, and "we forgot to pass an origin" would look
# identical to "this environment has no browser client". Every environment has
# one — the app is the only shipping client — so every root passes its own
# (https://app.insolvia.ai, https://staging-app.insolvia.ai,
# http://localhost:3000).
variable "cors_allowed_origins" {
  description = "Browser origins allowed to PUT to (and GET from) a presigned URL for this bucket. The app's origin per environment; a list because staging also serves local web dev."
  type        = list(string)

  validation {
    condition     = length(var.cors_allowed_origins) > 0
    error_message = "At least one origin is required: with none, every browser upload preflight fails and the app cannot upload at all."
  }

  # A browser sends `Origin: https://app.insolvia.ai` — scheme, host, optional
  # port, and NEVER a path or a trailing slash. S3 compares the value literally,
  # so "https://app.insolvia.ai/" is a configuration that applies to nothing and
  # fails exactly like a missing entry: a 403 on the preflight, with a CORS
  # configuration sitting right there in the console looking correct. Caught
  # here rather than on staging.
  validation {
    condition = alltrue([
      for origin in var.cors_allowed_origins :
      can(regex("^https?://[^/]+$", origin))
    ])
    error_message = "Each origin must be scheme://host[:port] with no path and no trailing slash — that is the literal value a browser sends, and S3 matches it literally."
  }
}

variable "api_role_name" {
  description = "Execution role that brokers uploads and downloads. Null in infra/envs/dev, where there is no Lambda and the developer's own IAM user is the principal."
  type        = string
  default     = null
}

variable "force_destroy" {
  description = "Whether `terraform destroy` may empty the bucket first. True in staging and dev (synthetic documents, disposable), false in prod."
  type        = bool
  default     = false
}

variable "tags" {
  description = "Tags applied to the bucket."
  type        = map(string)
  default     = {}
}
