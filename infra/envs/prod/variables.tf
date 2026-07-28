variable "aws_region" {
  description = "Default AWS region."
  type        = string
  default     = "us-east-1"
}

variable "domain_name" {
  description = "Apex domain for Insolvia."
  type        = string
  default     = "insolvia.ai"
}

variable "subdomain" {
  description = "Hostname this environment serves."
  type        = string
  default     = "app.insolvia.ai"
}

variable "api_subdomain" {
  description = "Hostname the backend API serves in this environment."
  type        = string
  default     = "api.insolvia.ai"
}

variable "mailer_subdomain" {
  description = "Hostname the mailer API serves in this environment."
  type        = string
  default     = "mailer-api.insolvia.ai"
}

# Singular `download`, matching staging's `staging-download.insolvia.ai` — the
# host is a place you download FROM, and the flat `staging-` prefix over there
# is what keeps both inside the single-label shared wildcard cert (D2).
variable "download_subdomain" {
  description = "Hostname the unsigned desktop build artifacts are served from in this environment."
  type        = string
  default     = "download.insolvia.ai"
}

variable "marketing_image_tag" {
  description = "ECR image tag the marketing SSR Lambda is seeded from (creation-time only; CI owns the running image afterwards). Defaults to this environment's moving marker tag — NOT `latest`, which under the shared insolvia-marketing repository would mean whatever any environment pushed last."
  type        = string
  default     = "prod"
}
