variable "project" {
  description = "Project slug, the first segment of the contract's SSM path."
  type        = string
  default     = "insolvia"
}

# The AUTH environment, which is not always the root's: staging and prod read
# their own, but every developer machine (environment dev-<machine_short_id>)
# reads the one shared pool's, "dev".
variable "environment" {
  description = "Environment whose auth contract to read (/<project>/<environment>/auth/*): staging, prod or dev."
  type        = string
}
