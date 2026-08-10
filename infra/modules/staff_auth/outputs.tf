output "user_pool_id" {
  description = "Staff Cognito user pool ID."
  value       = aws_cognito_user_pool.staff.id
}

output "user_pool_arn" {
  description = "Staff Cognito user pool ARN."
  value       = aws_cognito_user_pool.staff.arn
}

output "web_client_id" {
  description = "App client ID for the admin portal SPA (authorization-code + PKCE)."
  value       = aws_cognito_user_pool_client.web.id
}

# Always the prefix domain — this module has no custom-domain seam (see the
# header comment in main.tf). Named `domain`, matching modules/auth, so the
# portal build reads the same output shape from either pool.
output "domain" {
  description = "Hostname serving /oauth2/authorize, /oauth2/token and the staff sign-in pages (Cognito prefix domain)."
  value       = "${aws_cognito_user_pool_domain.staff.domain}.auth.${data.aws_region.current.region}.amazoncognito.com"
}

# The seam for the admin service: it verifies staff JWTs against this issuer
# (JWKS at <issuer>/.well-known/jwks.json) — and against ONLY this issuer,
# which is what keeps a firm user's token out of the admin surface.
output "issuer_url" {
  description = "OIDC issuer URL the admin service will validate staff access tokens against."
  value       = "https://cognito-idp.${data.aws_region.current.region}.amazonaws.com/${aws_cognito_user_pool.staff.id}"
}
