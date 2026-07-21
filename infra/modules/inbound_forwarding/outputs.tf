output "forwarder_lambda_function_name" {
  description = "Name of the inbound mail forwarder Lambda"
  value       = aws_lambda_function.forwarder.function_name
}

output "inbound_bucket" {
  description = "S3 bucket holding raw inbound mail"
  value       = aws_s3_bucket.inbound.id
}

output "dlq_url" {
  description = "URL of the forwarder dead-letter queue"
  value       = aws_sqs_queue.dlq.id
}

output "alerts_topic_arn" {
  description = "SNS topic the forwarder alarms publish to. A human must subscribe to this and confirm the subscription — Terraform does not manage subscriptions."
  value       = aws_sns_topic.alerts.arn
}

output "receipt_rule_set_name" {
  description = "Active SES receipt rule set name"
  value       = aws_ses_receipt_rule_set.main.rule_set_name
}

output "forwarded_recipients" {
  description = "Addresses SES accepts and forwards. no-reply@ is excluded by design (send-only)."
  value       = local.forwarded_recipients
}

output "forward_to_ssm_parameter" {
  description = "SSM SecureString path holding the private forward destination"
  value       = aws_ssm_parameter.forward_to.name
}
