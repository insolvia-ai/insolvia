output "user_pool_id" {
  description = "Cognito user pool ID."
  value       = aws_cognito_user_pool.main.id
}

output "user_pool_arn" {
  description = "Cognito user pool ARN (for a future API Gateway/JWT authorizer)."
  value       = aws_cognito_user_pool.main.arn
}

output "web_client_id" {
  description = "App client ID for the web SPA (authorization-code + PKCE)."
  value       = aws_cognito_user_pool_client.web.id
}

# The one hostname consumers should ever use: the app builds its /oauth2
# URLs from this, the E2E asserts the redirect landed on it, and both read it
# from the env's Terraform output rather than hard-coding either form.
#
# Prefers the custom domain where one exists, which is what makes the cutover a
# rebuild rather than a code change — the prefix domain keeps serving until the
# app's next deploy picks this up.
output "domain" {
  description = "Hostname serving /oauth2/authorize, /oauth2/token and the sign-in pages — the custom domain where configured, otherwise the Cognito prefix domain."
  value = (
    var.custom_domain == null
    ? "${aws_cognito_user_pool_domain.main.domain}.auth.${data.aws_region.current.region}.amazoncognito.com"
    : var.custom_domain
  )
}

# The prefix domain specifically, which keeps serving after a custom domain is
# added. Exposed so the cutover is observable — during it the two differ, and
# afterwards this is what you would remove.
output "prefix_domain" {
  description = "Cognito-provided prefix hostname. Always present; equals `domain` when no custom domain is configured."
  value       = "${aws_cognito_user_pool_domain.main.domain}.auth.${data.aws_region.current.region}.amazoncognito.com"
}

# The seam for the API (#65): the first authenticated endpoint verifies JWTs
# against this issuer (its JWKS lives at <issuer>/.well-known/jwks.json).
# services/api is deliberately untouched in this PR — the wiring belongs to
# that endpoint's PR.
output "issuer_url" {
  description = "OIDC issuer URL the API will validate access/ID tokens against."
  value       = "https://cognito-idp.${data.aws_region.current.region}.amazonaws.com/${aws_cognito_user_pool.main.id}"
}

# The MCP service's client-id allowlist (#261), derived from the registered
# harness clients so registering a harness IS adding it to the allowlist —
# the env publishes this as /insolvia/<env>/mcp/auth-client-ids and the
# deploy workflow derives AUTH_CLIENT_IDS from it. A map too, for anything
# that needs to name one harness's client (the E2E, a directory listing).
output "mcp_client_ids" {
  description = "Comma-joinable list of MCP harness app client IDs — the MCP service's token allowlist."
  value       = [for client in aws_cognito_user_pool_client.mcp : client.id]
}

output "mcp_client_ids_by_harness" {
  description = "MCP harness app client IDs, keyed by harness (claude, inspector...)."
  value       = { for harness, client in aws_cognito_user_pool_client.mcp : harness => client.id }
}
