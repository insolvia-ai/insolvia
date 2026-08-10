variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (staging, prod)."
  type        = string
}

variable "domain_name" {
  description = "Fully-qualified hostname to serve (e.g. staging-app.insolvia.ai)."
  type        = string
}

variable "hosted_zone_id" {
  description = "Route53 hosted zone ID for insolvia.ai."
  type        = string
}

variable "acm_certificate_arn" {
  description = "ARN of the *.insolvia.ai ACM certificate (must be in us-east-1)."
  type        = string
}

variable "tags" {
  description = "Common tags applied to all resources."
  type        = map(string)
  default     = {}
}

# See the local in main.tf: null derives the original insolvia-web-<env>; a
# second instantiation in the same environment must set this (bucket names and
# the OAC name derive from it and would otherwise collide).
variable "bucket_name" {
  description = "Origin bucket name, or null to derive <project>-web-<environment>. Keep the insolvia-web- prefix."
  type        = string
  default     = null

  validation {
    condition     = var.bucket_name == null || can(regex("^insolvia-web-", var.bucket_name))
    error_message = "bucket_name must keep the insolvia-web- prefix (the deploy role's S3 grant is scoped to it)."
  }
}
