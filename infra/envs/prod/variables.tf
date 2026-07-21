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

variable "marketing_image_tag" {
  description = "ECR image tag the marketing SSR Lambda is created from (creation-time only; CI owns it afterwards)."
  type        = string
  default     = "latest"
}
