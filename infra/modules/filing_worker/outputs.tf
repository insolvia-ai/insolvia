output "function_name" {
  description = "The worker Lambda's name — the deploy target of filing-<env>.yml. null in dev."
  value       = local.worker_count == 0 ? null : aws_lambda_function.worker[0].function_name
}

output "kill_switch_parameter" {
  description = "The kill switch's SSM parameter name. \"true\" lets the worker submit."
  value       = aws_ssm_parameter.kill_switch.name
}

output "worker_policy" {
  description = "The worker role's case-data grant, as rendered — pinned whole by tests/filing_worker.tftest.hcl."
  value       = local.worker_policy
}

output "ecr_repository_url" {
  description = "Passed through: the repository filing-<env>.yml pushes to. null in dev."
  value       = var.ecr_repository_url
}
