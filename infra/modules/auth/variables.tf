variable "project" {
  description = "Project slug, used in resource names."
  type        = string
  default     = "insolvia"
}

# The AUTH environment whose pool this grants on: staging or prod.
variable "environment" {
  description = "Auth environment (staging, prod) — the contract path /<project>/<environment>/auth/*."
  type        = string
}

# Everything that configures the pool and its clients — web_origins,
# custom_domain, deletion_protection, ses_source_arn, mcp_inspector_client —
# is insolvia-services' now (its infra/envs/<env>/main.tf), and so is the
# sign-in page's style. What is left here is what this repo grants.

# ── The API's invite grant ──────────────────────────────────────
# Name of the API's Lambda execution role, or null where there is no Lambda
# (dev — the local API runs as a dev server under the developer's own IAM
# user). Same optional shape, and the same reason, as
# modules/case_store's.
variable "api_role_name" {
  description = "API Lambda execution role name to grant AdminCreateUser on this pool, or null."
  type        = string
  default     = null
}

# The admin service's Lambda execution role (#213), or null where there is no
# admin Lambda (dev). Same shape, and the same one-action grant, as
# api_role_name — see the admin_invite resource.
variable "admin_invite_role_name" {
  description = "Admin Lambda execution role name to grant AdminCreateUser on this pool, or null."
  type        = string
  default     = null
}
