output "queue_url" {
  description = "SQS queue URL the API enqueues jobs onto — published to /insolvia/<env>/api/job-queue-url by the env root and derived into JOB_QUEUE_URL."
  value       = aws_sqs_queue.jobs.url
}

output "queue_arn" {
  description = "Job queue ARN."
  value       = aws_sqs_queue.jobs.arn
}

output "dlq_url" {
  description = "Dead-letter queue URL — where a job's message parks after maxReceiveCount failed attempts."
  value       = aws_sqs_queue.jobs_dlq.url
}

output "worker_function_name" {
  description = "Worker Lambda function name (deploy target for update-function-code/-configuration). null where the worker half is absent (dev)."
  value       = local.worker_count == 0 ? null : aws_lambda_function.worker[0].function_name
}

output "worker_role_name" {
  description = "Worker Lambda execution role name — what modules/case_store's worker_role_name takes to attach the table grant. null in dev."
  value       = local.worker_count == 0 ? null : aws_iam_role.worker[0].name
}

output "ecr_repository_url" {
  description = "Passed straight through, like api_service's — the repository the deploy workflow pushes worker images to. null in dev."
  value       = var.ecr_repository_url
}
