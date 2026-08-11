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

variable "marketing_image_tag" {
  description = "ECR image tag the marketing SSR Lambda is seeded from (creation-time only; CI owns the running image afterwards). Defaults to this environment's moving marker tag — NOT `latest`, which under the shared insolvia-marketing repository would mean whatever any environment pushed last."
  type        = string
  default     = "prod"
}

# The admin SERVICE's host (#213); the portal serves from admin_subdomain
# when its hosting lands (#215).
variable "admin_api_subdomain" {
  description = "Hostname the admin service serves in this environment."
  type        = string
  default     = "admin-api.insolvia.ai"
}

# A PUBLIC value, committed on purpose — #209 records the decision. This is
# production's client; nothing running elsewhere can satisfy its audience.
variable "google_admin_client_id" {
  description = "Google Workspace OAuth client id staff ID tokens must carry as their audience."
  type        = string
  default     = "925851246989-115l1fsln1ntv52uv3bhg29k819fram7.apps.googleusercontent.com"
}

# The admin PORTAL's host (#215) — the static SPA; the admin SERVICE lives at
# admin_api_subdomain above. Same flat-label reasoning as every subdomain here.
variable "admin_subdomain" {
  description = "Hostname the admin portal serves in this environment."
  type        = string
  default     = "admin.insolvia.ai"
}
