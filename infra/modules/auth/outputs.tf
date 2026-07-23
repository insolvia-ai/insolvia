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

output "desktop_client_id" {
  description = "App client ID for the desktop app (loopback-redirect PKCE)."
  value       = aws_cognito_user_pool_client.desktop.id
}

output "domain" {
  description = "Hosted auth domain (Cognito-provided) serving /oauth2/authorize, /oauth2/token, and the sign-in pages."
  value       = "${aws_cognito_user_pool_domain.main.domain}.auth.${data.aws_region.current.name}.amazoncognito.com"
}

# The seam for the API (#65): the first authenticated endpoint verifies JWTs
# against this issuer (its JWKS lives at <issuer>/.well-known/jwks.json).
# services/api is deliberately untouched in this PR — the wiring belongs to
# that endpoint's PR.
output "issuer_url" {
  description = "OIDC issuer URL the API will validate access/ID tokens against."
  value       = "https://cognito-idp.${data.aws_region.current.name}.amazonaws.com/${aws_cognito_user_pool.main.id}"
}
