# The names every env root, SSM parameter and deploy workflow has always read
# from module.auth — now passed through from modules/auth_contract, which
# reads them from insolvia-services' SSM contract.

output "user_pool_id" {
  description = "Cognito user pool ID."
  value       = module.contract.user_pool_id
}

output "user_pool_arn" {
  description = "Cognito user pool ARN."
  value       = module.contract.user_pool_arn
}

output "web_client_id" {
  description = "App client ID for the web SPA (authorization-code + PKCE)."
  value       = module.contract.web_client_id
}

# The client portal's app client (ADR 0023) — DISJOINT from web_client_id.
# Envs publish it as /insolvia/<env>/api/auth-portal-client-id and as the
# `auth_portal_client_id` output the app build and the contract suite's deployed target read.
output "portal_client_id" {
  description = "App client ID for the client portal (authorization-code + PKCE, memory-only sessions)."
  value       = module.contract.portal_client_id
}

output "domain" {
  description = "Hostname serving /oauth2/authorize, /oauth2/token and the sign-in pages — the custom domain where configured, otherwise the Cognito prefix domain."
  value       = module.contract.domain
}

output "issuer_url" {
  description = "OIDC issuer URL the API validates access/ID tokens against."
  value       = module.contract.issuer_url
}

output "mcp_client_ids" {
  description = "Comma-joinable list of MCP harness app client IDs — the MCP service's token allowlist."
  value       = module.contract.mcp_client_ids
}

output "mcp_client_ids_by_harness" {
  description = "MCP harness app client IDs, keyed by harness (claude, inspector...)."
  value       = module.contract.mcp_client_ids_by_harness
}
