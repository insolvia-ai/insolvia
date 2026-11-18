# The environment's PostgreSQL database (ADR 0030): one Aurora Serverless v2
# cluster, PostgreSQL 17, minimum capacity 0, storage under its own
# customer-managed key, in the private subnets of modules/network.
#
# Who connects, and how:
#   • Every service connects with IAM database authentication as its own
#     Postgres role (var.iam_users: api, worker, admin, mcp, filing). The
#     grant here is rds-db:connect for exactly that role name; what the role
#     may do inside the database is the migrations' GRANTs (ADR 0030 § Sealed
#     data and logs), not this file's.
#   • The master user IS the `migrator` role. Its password is generated and
#     held by RDS in Secrets Manager (manage_master_user_password) — never in
#     Terraform state, never in this public repo — and the migrator Lambda is
#     the one principal whose role may read that secret. The migrations it
#     runs create the service roles and `GRANT rds_iam` to them.
#
# Never applied while AWS is paused (docs/runbooks/aws-paused.md).

locals {
  name       = "${var.project}-${var.environment}-database"
  account_id = data.aws_caller_identity.current.account_id
  region     = data.aws_region.current.region

  migrator_role_arn = "arn:aws:iam::${local.account_id}:role/${var.project}-${var.environment}-migrator-role"
  rds_service_role  = "arn:aws:iam::${local.account_id}:role/aws-service-role/rds.amazonaws.com/AWSServiceRoleForRDS"

  # Who may Decrypt what (pinned whole by tests/database.tftest.hcl):
  #   • Nobody decrypts with this key directly. Every Decrypt must come
  #     through RDS (the cluster's storage) or Secrets Manager (the master
  #     secret) — so a stolen credential with kms:* still opens nothing
  #     outside those two services.
  #   • Through Secrets Manager, only the migrator's role (and RDS itself,
  #     which rotates the secret) may open the master password. The services
  #     never need it: they authenticate with IAM.
  key_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid       = "EnableIAMPolicies"
        Effect    = "Allow"
        Principal = { AWS = "arn:aws:iam::${local.account_id}:root" }
        Action    = "kms:*"
        Resource  = "*"
      },
      {
        Sid       = "DenyDecryptOutsideRdsAndSecretsManager"
        Effect    = "Deny"
        Principal = { AWS = "*" }
        Action    = "kms:Decrypt"
        Resource  = "*"
        Condition = {
          StringNotEquals = {
            "kms:ViaService" = [
              "rds.${local.region}.amazonaws.com",
              "secretsmanager.${local.region}.amazonaws.com",
            ]
          }
        }
      },
      {
        Sid       = "DenyMasterSecretToAllButMigrator"
        Effect    = "Deny"
        Principal = { AWS = "*" }
        Action    = "kms:Decrypt"
        Resource  = "*"
        Condition = {
          StringEquals    = { "kms:ViaService" = "secretsmanager.${local.region}.amazonaws.com" }
          StringNotEquals = { "aws:PrincipalArn" = [local.migrator_role_arn, local.rds_service_role] }
        }
      },
    ]
  })
}

data "aws_caller_identity" "current" {}
data "aws_region" "current" {}

resource "aws_kms_key" "database" {
  description             = "${local.name}: Aurora storage and its master secret"
  enable_key_rotation     = true
  deletion_window_in_days = var.key_deletion_window_in_days
  policy                  = local.key_policy
  tags                    = var.tags
}

resource "aws_kms_alias" "database" {
  name          = "alias/${local.name}"
  target_key_id = aws_kms_key.database.key_id
}

resource "aws_db_subnet_group" "database" {
  name       = local.name
  subnet_ids = var.subnet_ids
  tags       = var.tags
}

resource "aws_security_group" "database" {
  name        = local.name
  description = "Aurora: Postgres from the backend Lambdas only."
  vpc_id      = var.vpc_id
  tags        = var.tags
}

resource "aws_vpc_security_group_ingress_rule" "postgres_from_lambdas" {
  security_group_id            = aws_security_group.database.id
  description                  = "Postgres from the backend Lambdas"
  referenced_security_group_id = var.client_security_group_id
  ip_protocol                  = "tcp"
  from_port                    = 5432
  to_port                      = 5432
}

resource "aws_rds_cluster" "database" {
  cluster_identifier = local.name
  engine             = "aurora-postgresql"
  engine_mode        = "provisioned" # Serverless v2 is "provisioned" with db.serverless instances
  engine_version     = var.engine_version
  database_name      = "insolvia"

  master_username               = "migrator"
  manage_master_user_password   = true
  master_user_secret_kms_key_id = aws_kms_key.database.arn

  iam_database_authentication_enabled = true

  storage_encrypted = true
  kms_key_id        = aws_kms_key.database.arn

  db_subnet_group_name   = aws_db_subnet_group.database.name
  vpc_security_group_ids = [aws_security_group.database.id]

  serverlessv2_scaling_configuration {
    min_capacity             = var.min_capacity
    max_capacity             = var.max_capacity
    seconds_until_auto_pause = var.min_capacity == 0 ? 300 : null
  }

  backup_retention_period   = var.backup_retention_days
  copy_tags_to_snapshot     = true
  deletion_protection       = var.deletion_protection
  skip_final_snapshot       = !var.deletion_protection
  final_snapshot_identifier = var.deletion_protection ? "${local.name}-final" : null

  tags = var.tags
}

resource "aws_rds_cluster_instance" "database" {
  identifier          = "${local.name}-1"
  cluster_identifier  = aws_rds_cluster.database.id
  engine              = aws_rds_cluster.database.engine
  engine_version      = aws_rds_cluster.database.engine_version
  instance_class      = "db.serverless"
  publicly_accessible = false
  tags                = var.tags
}

# ── IAM database authentication: one Postgres role per service ──
resource "aws_iam_role_policy" "connect" {
  for_each = var.iam_users

  name = "${local.name}-connect"
  role = each.value
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid      = "ConnectAs${title(each.key)}"
      Effect   = "Allow"
      Action   = "rds-db:connect"
      Resource = "arn:aws:rds-db:${local.region}:${local.account_id}:dbuser:${aws_rds_cluster.database.cluster_resource_id}/${each.key}"
    }]
  })
}

# ── The migrator ─────────────────────────────────────────────────
# A one-shot Lambda the deploy workflow invokes before it shifts the API's
# alias (api-<env>.yml), applying insolvia_core's numbered migrations as the
# master user. Same image as the API, a different handler.
resource "aws_iam_role" "migrator" {
  name = "${var.project}-${var.environment}-migrator-role"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "lambda.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
  tags = var.tags
}

# Logs plus the ENI actions a Lambda in a VPC needs.
resource "aws_iam_role_policy_attachment" "migrator_vpc" {
  role       = aws_iam_role.migrator.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
}

resource "aws_iam_role_policy" "migrator_secret" {
  name = "${local.name}-master-secret"
  role = aws_iam_role.migrator.name
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "ReadMasterSecret"
        Effect   = "Allow"
        Action   = "secretsmanager:GetSecretValue"
        Resource = aws_rds_cluster.database.master_user_secret[0].secret_arn
      },
      {
        Sid      = "OpenMasterSecret"
        Effect   = "Allow"
        Action   = "kms:Decrypt"
        Resource = aws_kms_key.database.arn
      },
    ]
  })
}

resource "aws_lambda_function" "migrator" {
  function_name = "${var.project}-${var.environment}-migrator"
  role          = aws_iam_role.migrator.arn
  package_type  = "Image"
  image_uri     = var.migrator_image_uri
  timeout       = 300 # an auto-paused cluster takes ~15 s to resume
  memory_size   = 256

  image_config {
    command = ["insolvia_core.adapters.postgres.migrations.handler"]
  }

  vpc_config {
    subnet_ids         = var.subnet_ids
    security_group_ids = [var.client_security_group_id]
  }

  environment {
    variables = {
      INSOLVIA_ENV        = var.insolvia_env
      DATABASE_HOST       = aws_rds_cluster.database.endpoint
      DATABASE_NAME       = aws_rds_cluster.database.database_name
      DATABASE_SECRET_ARN = aws_rds_cluster.database.master_user_secret[0].secret_arn
    }
  }

  # The deploy workflow points it at the image it is about to ship.
  lifecycle { ignore_changes = [image_uri] }

  tags       = var.tags
  depends_on = [aws_iam_role_policy_attachment.migrator_vpc]
}

resource "aws_cloudwatch_log_group" "migrator" {
  name              = "/aws/lambda/${aws_lambda_function.migrator.function_name}"
  retention_in_days = 30
  tags              = var.tags
}
