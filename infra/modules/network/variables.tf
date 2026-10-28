variable "project" {
  description = "Name prefix."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (staging, prod). Not used by infra/envs/dev: local development talks to Docker Postgres, not a VPC (ADR 0028 § Local, dev, staging, prod)."
  type        = string
}

variable "vpc_cidr" {
  description = "The VPC's IPv4 block, /16. Each environment's VPC is isolated (no peering, no transit), so the blocks may overlap; the roots still pass distinct ones so that a peering added later does not force a rebuild."
  type        = string
  default     = "10.0.0.0/16"

  validation {
    condition     = can(cidrhost(var.vpc_cidr, 0)) && endswith(var.vpc_cidr, "/16")
    error_message = "vpc_cidr must be a valid /16 block."
  }
}

variable "nat_instance_type" {
  description = "Instance type of the fck-nat egress instance. t4g.nano (arm64, ~5 Gbps burst) is what ADR 0028 chose; the AMI is looked up for arm64, so an x86 type will not launch."
  type        = string
  default     = "t4g.nano"
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default     = {}
}
