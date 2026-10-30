variable "name" {
  description = "The host's name stem (insolvia-<env>-api, …); the policies are <name>-network and <name>-db-connect."
  type        = string
}

variable "role_name" {
  description = "The host's Lambda execution role, by NAME (modules/case_store's seam note says why a name, not a lookup)."
  type        = string
}

variable "db_username" {
  description = "The Postgres role the host connects as — one of the service roles the migrator creates (api, worker, admin, mcp, filing). null: no rds-db:connect grant here (the migrator, whose two logins modules/migrator grants itself)."
  type        = string
  default     = null

  validation {
    condition     = var.db_username == null || contains(["api", "worker", "admin", "mcp", "filing"], coalesce(var.db_username, "-"))
    error_message = "db_username must be one of the service roles the migrator creates: api, worker, admin, mcp, filing."
  }
}

variable "db_cluster_resource_id" {
  description = "The cluster's immutable resource id (module.aurora.cluster_resource_id) — what an rds-db:connect ARN names."
  type        = string
  default     = null
}
