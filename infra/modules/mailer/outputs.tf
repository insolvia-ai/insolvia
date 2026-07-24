output "api_url" {
  description = "Public HTTPS base URL for the mailer API (SigV4-authenticated; only insolvia_api's caller role can invoke it)."
  value       = "https://${var.domain_name}"
}

output "ecr_repository_url" {
  description = "ECR repository the deploy workflow pushes mailer images to."
  value       = aws_ecr_repository.mailer.repository_url
}

output "lambda_function_names" {
  description = "Mailer Lambda function names keyed by role — deploy target for update-function-code, one per entrypoint sharing the one image."
  value = {
    ingress  = aws_lambda_function.ingress.function_name
    sender   = aws_lambda_function.sender.function_name
    feedback = aws_lambda_function.feedback.function_name
  }
}

output "content_bucket" {
  description = "S3 bucket holding request manifests and (once attachments ship) uploaded attachment bytes."
  value       = aws_s3_bucket.content.id
}

output "messages_table" {
  description = "DynamoDB table tracking per-message admission/send/delivery status."
  value       = aws_dynamodb_table.messages.name
}

output "suppressions_table" {
  description = "DynamoDB table of recipients suppressed by a prior bounce/complaint."
  value       = aws_dynamodb_table.suppressions.name
}

output "service_registry_json" {
  description = "MAILER_SERVICE_REGISTRY_JSON value — the insolvia_api tenant's full config, for the API service (PR4) to reference or reproduce."
  value       = local.service_registry_json
}

output "configuration_set_registry_json" {
  description = "MAILER_CONFIGURATION_SET_REGISTRY_JSON value."
  value       = local.configuration_set_registry_json
}

output "api_status_queue_url" {
  description = "SQS queue insolvia_api's send-status events are published to (PR4's API service will consume this)."
  value       = aws_sqs_queue.api_status.url
}

output "api_status_queue_arn" {
  description = "ARN of the above, for the PR4 consumer's IAM policy / event source mapping."
  value       = aws_sqs_queue.api_status.arn
}

output "configuration_set" {
  description = "The one SES configuration set this environment's mail flows through."
  value       = local.configuration_set
}

output "alarms_topic_arn" {
  description = "SNS topic the mailer alarms (Lambda errors, DLQ, queue age, rejects, SES bounce/complaint rate, attachment scanning) publish to. A human must subscribe and confirm — Terraform does not manage subscriptions."
  value       = aws_sns_topic.alarms.arn
}
