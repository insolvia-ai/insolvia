# The same names, and the same values, modules/auth had when it created the
# pool itself, so every root, SSM parameter and deploy workflow that read
# module.auth.<x> reads it unchanged.

output "user_pool_id" {
  description = "Cognito user pool ID."
  value       = local.value["user-pool-id"]
}

output "user_pool_arn" {
  description = "Cognito user pool ARN."
  value       = local.value["user-pool-arn"]
}

output "web_client_id" {
  description = "App client ID for the web SPA (authorization-code + PKCE)."
  value       = local.value["client-id"]
}

# DISJOINT from web_client_id (ADR 0023): that disjointness is the audience
# check between the staff and client principal classes.
output "portal_client_id" {
  description = "App client ID for the client portal (authorization-code + PKCE, memory-only sessions)."
  value       = local.value["portal-client-id"]
}

# The pool's custom domain wherever one exists, else its prefix domain —
# decided where the pool is, so prod's future auth.insolvia.ai reaches the app
# on its next build with no change here.
output "domain" {
  description = "Hostname serving /oauth2/authorize, /oauth2/token and the sign-in pages."
  value       = local.value["domain"]
}

output "issuer_url" {
  description = "OIDC issuer URL the API and the MCP service verify tokens against."
  value       = local.value["issuer-url"]
}

# Ordered by harness name — the order the client for_each in insolvia-services
# iterates in, so the joined allowlist string is unchanged by the move.
output "mcp_client_ids" {
  description = "Comma-joinable list of MCP harness app client IDs — the MCP service's token allowlist."
  value       = [for harness in sort(keys(local.mcp_clients)) : local.mcp_clients[harness]]
}

output "mcp_client_ids_by_harness" {
  description = "MCP harness app client IDs, keyed by harness (claude, inspector...)."
  value       = local.mcp_clients
}
