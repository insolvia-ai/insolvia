output "bucket_name" {
  description = "S3 bucket the production web build is synced to."
  value       = module.web_hosting.bucket_name
}

output "distribution_id" {
  description = "CloudFront distribution ID for production (cache invalidation)."
  value       = module.web_hosting.distribution_id
}

output "url" {
  value = module.web_hosting.url
}

output "api_ecr_repository_url" {
  description = "ECR repository the API deploy workflow pushes production images to."
  value       = module.api_service.ecr_repository_url
}

output "api_lambda_function_name" {
  description = "Production API Lambda name (deploy target for update-function-code/-configuration)."
  value       = module.api_service.lambda_function_name
}

output "api_domain" {
  description = "Hostname the production API serves."
  value       = module.api_service.domain_name
}

output "api_http_api_id" {
  description = "Production HTTP API id."
  value       = module.api_service.http_api_id
}

output "api_waitlist_table_name" {
  description = "Production waitlist DynamoDB table."
  value       = module.api_service.waitlist_table_name
}

output "api_alarms_topic_arn" {
  description = "SNS topic for production API alarms — subscribe by hand (Terraform manages no subscriptions)."
  value       = module.api_service.alarms_topic_arn
}

output "auth_user_pool_id" {
  description = "Production Cognito user pool ID."
  value       = module.auth.user_pool_id
}

output "auth_user_pool_arn" {
  description = "Production Cognito user pool ARN."
  value       = module.auth.user_pool_arn
}

output "auth_web_client_id" {
  description = "Production web SPA app client ID (authorization-code + PKCE)."
  value       = module.auth.web_client_id
}

output "auth_desktop_client_id" {
  description = "Production desktop app client ID (loopback-redirect PKCE)."
  value       = module.auth.desktop_client_id
}

output "auth_domain" {
  description = "Production hosted auth domain (Cognito-provided)."
  value       = module.auth.domain
}

output "auth_issuer_url" {
  description = "OIDC issuer the API will validate production JWTs against (wired up with the first authenticated endpoint)."
  value       = module.auth.issuer_url
}

# ── Mailer (issues 6.2, 6.3) — read by the deploy workflow and, in PR4, by
# the API deploy workflow to derive its MAILER_* env vars ────────────────────
output "mailer_api_url" {
  description = "Production mailer API base URL (SigV4-authenticated)."
  value       = module.mailer.api_url
}

output "mailer_ecr_repository_url" {
  description = "ECR repository the mailer deploy workflow pushes production images to."
  value       = module.mailer.ecr_repository_url
}

output "mailer_lambda_function_names" {
  description = "Production mailer Lambda function names keyed by role (ingress/sender/feedback)."
  value       = module.mailer.lambda_function_names
}

output "mailer_content_bucket" {
  description = "Production mailer content bucket (request manifests, future attachments)."
  value       = module.mailer.content_bucket
}

output "mailer_messages_table" {
  description = "Production mailer messages DynamoDB table."
  value       = module.mailer.messages_table
}

output "mailer_suppressions_table" {
  description = "Production mailer suppressions DynamoDB table."
  value       = module.mailer.suppressions_table
}

output "mailer_service_registry_json" {
  description = "Production MAILER_SERVICE_REGISTRY_JSON value — the insolvia_api tenant config, for PR4's API service to reference."
  value       = module.mailer.service_registry_json
}

output "mailer_api_status_queue_url" {
  description = "Production SQS queue insolvia_api's send-status events publish to."
  value       = module.mailer.api_status_queue_url
}

output "mailer_api_status_queue_arn" {
  description = "ARN of the above, for PR4's consumer IAM policy / event source mapping."
  value       = module.mailer.api_status_queue_arn
}

output "mailer_configuration_set" {
  description = "Production SES configuration set the mailer sends through."
  value       = module.mailer.configuration_set
}

# ── Marketing site (www.insolvia.ai) — read by the deploy workflow ──
output "marketing_distribution_id" {
  description = "Marketing CloudFront distribution ID (cache invalidation)."
  value       = module.marketing_site.distribution_id
}

output "marketing_assets_bucket_name" {
  description = "S3 bucket the marketing client build is synced to."
  value       = module.marketing_site.assets_bucket_name
}

output "marketing_ecr_repository_url" {
  description = "ECR repository URL for the marketing SSR image."
  value       = module.marketing_site.ecr_repository_url
}

output "marketing_ssr_function_name" {
  description = "Marketing SSR Lambda function name."
  value       = module.marketing_site.ssr_function_name
}

output "marketing_url" {
  value = module.marketing_site.url
}
