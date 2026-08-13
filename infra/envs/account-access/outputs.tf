output "human_user_arns" {
  description = "ARNs of the account's human IAM users, keyed by user name."
  value       = { for name, user in aws_iam_user.human : name => user.arn }
}

output "admin_group_arn" {
  description = "ARN of the insolvia-shared-admin-group (AdministratorAccess)."
  value       = aws_iam_group.admin.arn
}

output "group_membership" {
  description = "Who is in which group — the answer to \"who can do what here\" without walking the console."
  value       = { for name, user in var.human_users : name => user.groups }
}
