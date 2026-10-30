# ── A backend Lambda's way into the VPC and the database (ADR 0028) ─────────
# Every .NET host runs in modules/network's private subnets and reaches Aurora
# as its own Postgres role. That needs two grants on the host's execution role,
# the same two for every host — so they have one owner, here, instead of six
# copies drifting apart:
#
#   <name>-network     the elastic network interfaces Lambda creates in the
#                      subnets (what AWS's AWSLambdaVPCAccessExecutionRole
#                      holds). An INLINE copy, not that managed policy:
#                      insolvia-services' ci-trust lets the deploy role attach
#                      exactly one AWS-managed policy (AWSLambdaBasicExecutionRole)
#                      and nothing else, which is a control worth keeping.
#                      CreateNetworkInterface has no useful resource fence (the
#                      ENI does not exist yet), so the resources are "*", as in
#                      the managed policy.
#   <name>-db-connect  rds-db:connect for ONE Postgres role on ONE cluster —
#                      arn:aws:rds-db:<region>:<account>:dbuser:<cluster
#                      resource id>/<role>. The role name is the same literal
#                      the service's SSM namespace publishes as
#                      INSOLVIA_DB_USERNAME, and the migrator creates it WITH
#                      rds_iam (backend/src/Insolvia.Migrator/RoleProvisioner.cs).
#                      A host asking for any other role is refused by IAM
#                      before Postgres is asked. Omitted for the migrator, which
#                      signs in as the RDS master from the managed secret.
#
# The Lambda that uses the role must wait for the network grant: Lambda checks
# the execution role for CreateNetworkInterface when vpc_config is applied and
# refuses the change if it is missing. Each caller lists this module in the
# function's depends_on.

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

resource "aws_iam_role_policy" "network" {
  name = "${var.name}-network"
  role = var.role_name
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid    = "LambdaNetworkInterfaces"
        Effect = "Allow"
        Action = [
          "ec2:CreateNetworkInterface",
          "ec2:DescribeNetworkInterfaces",
          "ec2:DescribeSubnets",
          "ec2:DeleteNetworkInterface",
          "ec2:AssignPrivateIpAddresses",
          "ec2:UnassignPrivateIpAddresses",
        ]
        Resource = "*"
      },
    ]
  })
}

resource "aws_iam_role_policy" "db_connect" {
  count = var.db_username == null ? 0 : 1

  name = "${var.name}-db-connect"
  role = var.role_name
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "DatabaseLogin"
        Effect   = "Allow"
        Action   = ["rds-db:connect"]
        Resource = "arn:aws:rds-db:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:dbuser:${coalesce(var.db_cluster_resource_id, "unset")}/${var.db_username}"
      },
    ]
  })

  lifecycle {
    precondition {
      condition     = var.db_cluster_resource_id != null
      error_message = "db_username needs db_cluster_resource_id (module.aurora.cluster_resource_id)."
    }
  }
}
