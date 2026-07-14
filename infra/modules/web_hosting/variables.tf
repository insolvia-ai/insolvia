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
  description = "Fully-qualified hostname to serve (e.g. staging.insolvia.ai)."
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
