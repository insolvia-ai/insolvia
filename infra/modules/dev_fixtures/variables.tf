variable "project" {
  description = "Name prefix. The bucket must stay in the insolvia-*-dev-fixtures-* family — ci-trust's DevFixtureBucket grant (deploy role) and DevFixtureObjectsRead grant (seed role) are scoped to exactly that prefix."
  type        = string
  default     = "insolvia"
}

variable "aws_region" {
  description = "Region, taken into the bucket name: S3 bucket names are globally unique, so every bucket here carries its region as the suffix (the insolvia-aws-naming skill)."
  type        = string
}

variable "tags" {
  description = "Tags applied to the bucket."
  type        = map(string)
  default     = {}
}
