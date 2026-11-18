variable "project" {
  description = "Project name — the first segment of every resource name."
  type        = string
  default     = "insolvia"
}

variable "environment" {
  description = "Environment name (staging, prod) — the second segment of every resource name."
  type        = string
}

variable "cidr_block" {
  description = "The VPC's address range. Private: nothing outside the VPC routes to it."
  type        = string
  default     = "10.0.0.0/16"
}

variable "nat_instance_type" {
  description = "The fck-nat instance's type. Arm (t4g) to match the AMI lookup below."
  type        = string
  default     = "t4g.nano"
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
  default     = {}
}
