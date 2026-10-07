output "queue_url" {
  description = "The filing queue's URL — published to /insolvia/<env>/api/filing-queue-url by the env root and derived into FILING_QUEUE_URL."
  value       = aws_sqs_queue.filing.url
}

output "queue_arn" {
  description = "The filing queue's ARN."
  value       = aws_sqs_queue.filing.arn
}

output "dlq_url" {
  description = "The filing queue's dead-letter queue URL."
  value       = aws_sqs_queue.filing_dlq.url
}

# The rendered grants, exposed so tests/filing_queue.tftest.hcl can pin them
# whole. Policies are not secrets; they are in every plan already.
output "enqueue_policy" {
  description = "The API role's send grant, as rendered."
  value       = local.enqueue_policy
}

output "consume_policy" {
  description = "The filing worker role's consume grant, as rendered."
  value       = local.consume_policy
}
