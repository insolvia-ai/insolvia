# The same output names, and the same values, this module had when it created
# the pool itself — now derived from the name lookups in main.tf. Every env
# root, SSM parameter and deploy workflow that read module.auth.<x> reads it
# unchanged.

output "user_pool_id" {
  description = "Cognito user pool ID."
  value       = local.user_pool_id
}

output "user_pool_arn" {
  description = "Cognito user pool ARN."
  value       = local.user_pool_arn
}

output "web_client_id" {
  description = "App client ID for the web SPA (authorization-code + PKCE)."
  value       = local.web_client_id
}

# The client portal's app client (ADR 0023) — DISJOINT from web_client_id,
# and that disjointness is the audience check between the staff and client
# principal classes. Envs publish it as /insolvia/<env>/api/auth-portal-client-id
# (the deploy workflow derives AUTH_PORTAL_CLIENT_ID from it) and as the
# `auth_portal_client_id` output the app build and the integration tier read.
output "portal_client_id" {
  description = "App client ID for the client portal (authorization-code + PKCE, memory-only sessions)."
  value       = local.portal_client_id
}

# The one hostname consumers should ever use: the app builds its /oauth2
# URLs from this, the E2E asserts the redirect landed on it, and both read it
# from the env's Terraform output rather than hard-coding either form.
#
# Prefers the pool's custom domain wherever one exists — read from the pool,
# not configured here — so when insolvia-services adds one (prod's
# auth.insolvia.ai is staged behind staging's), the app moves on its next
# build with no change in this repo, and the prefix keeps serving until then.
output "domain" {
  description = "Hostname serving /oauth2/authorize, /oauth2/token and the sign-in pages — the custom domain where configured, otherwise the Cognito prefix domain."
  value       = local.custom_domain == "" ? local.prefix_domain : local.custom_domain
}

output "prefix_domain" {
  description = "Cognito-provided prefix hostname. Always present; equals `domain` when no custom domain is configured."
  value       = local.prefix_domain
}

# What services/api and services/mcp verify tokens against (its JWKS lives at
# <issuer>/.well-known/jwks.json).
output "issuer_url" {
  description = "OIDC issuer URL the API validates access/ID tokens against."
  value       = "https://cognito-idp.${data.aws_region.current.region}.amazonaws.com/${local.user_pool_id}"
}

# The MCP service's client-id allowlist (#261): every insolvia-<env>-mcp-*
# client on the pool, so registering a harness in insolvia-services IS adding
# it to the allowlist. The env publishes this as
# /insolvia/<env>/mcp/auth-client-ids and the deploy workflow derives
# AUTH_CLIENT_IDS from it. Ordered by harness name — the order the for_each
# that creates them iterates in, so the joined string is unchanged by the move.
output "mcp_client_ids" {
  description = "Comma-joinable list of MCP harness app client IDs — the MCP service's token allowlist."
  value       = [for harness in sort(keys(local.mcp_clients)) : local.mcp_clients[harness]]
}

output "mcp_client_ids_by_harness" {
  description = "MCP harness app client IDs, keyed by harness (claude, inspector...)."
  value       = local.mcp_clients
}
