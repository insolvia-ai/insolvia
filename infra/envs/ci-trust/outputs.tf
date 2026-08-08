output "github_actions_role_arn" {
  description = "Set this as the AWS_ROLE_ARN GitHub secret. (Unchanged by the extraction from `shared` — the role keeps its name and ARN.)"
  value       = aws_iam_role.github_actions.arn
}

output "github_oidc_provider_arn" {
  description = "ARN of the account's GitHub OIDC provider."
  value       = aws_iam_openid_connect_provider.github.arn
}

# Deliberately a SECOND secret rather than reusing AWS_ROLE_ARN: the two roles
# exist to be different, and a job that picked up the deploy role here would
# silently get the wide one. Set it as an `insolvia-staging` ENVIRONMENT secret
# — this role's trust policy only accepts tokens minted for that environment,
# so a repository-scoped secret would be a value no job could use anyway.
output "github_seed_role_arn" {
  description = "Set this as the AWS_SEED_ROLE_ARN secret on the insolvia-staging environment."
  value       = aws_iam_role.github_seed.arn
}
