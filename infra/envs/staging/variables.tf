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
  default     = "staging.insolvia.ai"
}
