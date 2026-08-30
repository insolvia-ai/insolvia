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

# What this instantiation SERVES, and the third segment of every name it
# creates — see the local in main.tf. Each environment instantiates this module
# twice, once per component, so this is what keeps the two apart.
#
# It replaced a free-text `bucket_name` override. Naming the component rather
# than the bucket is what makes the region suffix and the insolvia-<env>- stem
# unskippable: a caller can no longer hand-write a name that omits either.
variable "component" {
  description = "The surface this instance serves: `app` (the Expo SPA) or `admin` (the staff portal). Becomes insolvia-<env>-<component>."
  type        = string

  validation {
    condition     = contains(["app", "admin"], var.component)
    error_message = "component must be one of: app, admin. A new value needs a matching arn:aws:s3:::insolvia-*-<component>-* grant in infra/envs/ci-trust, which is human-applied."
  }
}
