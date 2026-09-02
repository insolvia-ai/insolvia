output "bucket_name" {
  description = "S3 bucket the staging web build is synced to."
  value       = module.web_hosting.bucket_name
}

output "distribution_id" {
  description = "CloudFront distribution ID for staging (cache invalidation)."
  value       = module.web_hosting.distribution_id
}

output "url" {
  value = module.web_hosting.url
}

output "api_ecr_repository_url" {
  description = "ECR repository the API deploy workflow pushes staging images to."
  value       = module.api_service.ecr_repository_url
}

output "api_lambda_function_name" {
  description = "Staging API Lambda name (deploy target for update-function-code/-configuration)."
  value       = module.api_service.lambda_function_name
}

output "api_lambda_alias_name" {
  description = "Staging API Lambda alias serving traffic. The deploy workflow shifts it only after the freshly published version passes its smoke test."
  value       = module.api_service.lambda_alias_name
}

output "api_domain" {
  description = "Hostname the staging API serves."
  value       = module.api_service.domain_name
}

output "api_http_api_id" {
  description = "Staging HTTP API id."
  value       = module.api_service.http_api_id
}

output "api_waitlist_table_name" {
  description = "Staging waitlist DynamoDB table."
  value       = module.api_service.waitlist_table_name
}

output "api_alarms_topic_arn" {
  description = "SNS topic for staging API alarms — subscribe by hand (Terraform manages no subscriptions)."
  value       = module.api_service.alarms_topic_arn
}

output "jobs_ecr_repository_url" {
  description = "ECR repository the API deploy workflow pushes staging worker images to (ADR 0018)."
  value       = module.job_pipeline.ecr_repository_url
}

output "jobs_worker_function_name" {
  description = "Staging pipeline worker Lambda name (deploy target for update-function-code/-configuration)."
  value       = module.job_pipeline.worker_function_name
}

output "jobs_queue_url" {
  description = "Staging job queue URL — also published to SSM as job-queue-url for the API."
  value       = module.job_pipeline.queue_url
}

output "auth_user_pool_id" {
  description = "Staging Cognito user pool ID."
  value       = module.auth.user_pool_id
}

# Also published to SSM (main.tf) for the API to read at runtime. This output
# exists for a different reader: app-staging.yml's seed step, which loads
# seeds/staging.json before the E2E suite runs and needs the table name in the
# workflow, not in a Lambda's environment. Reading SSM from the workflow would
# work too and was rejected — it needs an extra IAM grant on the seed role to
# fetch a value Terraform already knows and every other value in that job
# already comes from `terraform output`.
output "firm_table_name" {
  description = "Firms, firm users and their permissions (FIRM_TABLE_NAME for the API)."
  value       = module.firm_store.table_name
}

output "auth_user_pool_arn" {
  description = "Staging Cognito user pool ARN."
  value       = module.auth.user_pool_arn
}

output "auth_web_client_id" {
  description = "Staging web SPA app client ID (authorization-code + PKCE)."
  value       = module.auth.web_client_id
}

output "auth_domain" {
  description = "Staging hosted auth domain (Cognito-provided)."
  value       = module.auth.domain
}

output "auth_issuer_url" {
  description = "OIDC issuer the API will validate staging JWTs against (wired up with the first authenticated endpoint)."
  value       = module.auth.issuer_url
}

# ── Mailer (issues 6.2, 6.3) — read by the deploy workflow and, in PR4, by
# the API deploy workflow to derive its MAILER_* env vars ────────────────────
output "mailer_api_url" {
  description = "Staging mailer API base URL (SigV4-authenticated)."
  value       = module.mailer.api_url
}

output "mailer_ecr_repository_url" {
  description = "ECR repository the mailer deploy workflow pushes staging images to."
  value       = module.mailer.ecr_repository_url
}

output "mailer_lambda_function_names" {
  description = "Staging mailer Lambda function names keyed by role (ingress/sender/feedback)."
  value       = module.mailer.lambda_function_names
}

output "mailer_content_bucket" {
  description = "Staging mailer content bucket (request manifests, future attachments)."
  value       = module.mailer.content_bucket
}

output "mailer_messages_table" {
  description = "Staging mailer messages DynamoDB table."
  value       = module.mailer.messages_table
}

output "mailer_suppressions_table" {
  description = "Staging mailer suppressions DynamoDB table."
  value       = module.mailer.suppressions_table
}

output "mailer_service_registry_json" {
  description = "Staging MAILER_SERVICE_REGISTRY_JSON value — the insolvia_api tenant config, for PR4's API service to reference."
  value       = module.mailer.service_registry_json
}

output "mailer_api_status_queue_url" {
  description = "Staging SQS queue insolvia_api's send-status events publish to."
  value       = module.mailer.api_status_queue_url
}

output "mailer_api_status_queue_arn" {
  description = "ARN of the above, for PR4's consumer IAM policy / event source mapping."
  value       = module.mailer.api_status_queue_arn
}

output "mailer_configuration_set" {
  description = "Staging SES configuration set the mailer sends through."
  value       = module.mailer.configuration_set
}

output "mailer_alarms_topic_arn" {
  description = "SNS topic for staging mailer alarms — subscribe by hand (Terraform manages no subscriptions)."
  value       = module.mailer.alarms_topic_arn
}

# ── Marketing site (issue #45 revisited) — read by marketing-staging.yml ──
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

output "marketing_ssr_alias_name" {
  description = "Staging marketing SSR Lambda alias serving traffic. Shifted only after the freshly published version passes its smoke test."
  value       = module.marketing_site.ssr_alias_name
}

output "marketing_url" {
  value = module.marketing_site.url
}

output "admin_ecr_repository_url" {
  description = "Shared insolvia-admin repository the deploy workflow pushes to."
  value       = data.aws_ecr_repository.service["admin-api"].repository_url
}

output "admin_lambda_function_name" {
  description = "Admin Lambda function name (deploy target)."
  value       = module.admin_service.lambda_function_name
}

output "admin_lambda_alias_name" {
  description = "Alias serving admin traffic; the deploy workflow shifts it after the smoke test."
  value       = module.admin_service.lambda_alias_name
}

output "admin_api_url" {
  description = "Staging admin service base URL."
  value       = module.admin_service.url
}

output "admin_audit_table_name" {
  description = "Append-only admin audit table (#178's provisioning record)."
  value       = module.admin_service.audit_table_name
}

output "admin_alarms_topic_arn" {
  description = "SNS topic for staging admin alarms — subscribe by hand (Terraform manages no subscriptions)."
  value       = module.admin_service.alarms_topic_arn
}

output "admin_domain" {
  description = "Hostname the admin service serves (deploy smoke target)."
  value       = module.admin_service.domain_name
}

output "admin_web_bucket_name" {
  description = "Origin bucket the admin portal deploy syncs into."
  value       = module.admin_web_hosting.bucket_name
}

output "admin_web_distribution_id" {
  description = "Admin portal CloudFront distribution ID (cache invalidation)."
  value       = module.admin_web_hosting.distribution_id
}

output "admin_portal_url" {
  description = "Where the admin portal serves."
  value       = module.admin_web_hosting.url
}
