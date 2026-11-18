# Pins the database key's policy WHOLE — "who may decrypt what" (ADR 0030 §
# Sealed data and logs). Offline: the AWS provider is mocked, so
# `terraform test` needs no credentials and creates nothing:
#
#   terraform -chdir=infra/modules/database init -backend=false
#   terraform -chdir=infra/modules/database test
#
# shared-infra-plan.yml runs exactly that on every PR. A change to the key
# policy MUST change this file.

mock_provider "aws" {
  override_data {
    target = data.aws_caller_identity.current
    values = { account_id = "111122223333" }
  }
  override_data {
    target = data.aws_region.current
    values = { region = "us-east-1" }
  }
  mock_resource "aws_kms_key" {
    defaults = { arn = "arn:aws:kms:us-east-1:111122223333:key/database-key" }
  }
  mock_resource "aws_iam_role" {
    defaults = { arn = "arn:aws:iam::111122223333:role/insolvia-staging-migrator-role" }
  }
  mock_resource "aws_rds_cluster" {
    defaults = {
      cluster_resource_id = "cluster-ABCDEFGHIJ"
      endpoint            = "insolvia-staging-database.cluster-x.us-east-1.rds.amazonaws.com"
      master_user_secret  = [{ secret_arn = "arn:aws:secretsmanager:us-east-1:111122223333:secret:rds!cluster-x", kms_key_id = "k", secret_status = "active" }]
    }
  }
}

variables {
  environment              = "staging"
  insolvia_env             = "staging"
  vpc_id                   = "vpc-1"
  subnet_ids               = ["subnet-a", "subnet-b"]
  client_security_group_id = "sg-lambda"
  migrator_image_uri       = "111122223333.dkr.ecr.us-east-1.amazonaws.com/insolvia-shared-api:staging"
  deletion_protection      = false
  iam_users = {
    api    = "insolvia-staging-api-role"
    worker = "insolvia-staging-jobs-worker-role"
  }
}

run "key_policy_is_pinned" {
  command = apply

  assert {
    condition = output.key_policy == jsonencode({
      Version = "2012-10-17"
      Statement = [
        {
          Sid       = "EnableIAMPolicies"
          Effect    = "Allow"
          Principal = { AWS = "arn:aws:iam::111122223333:root" }
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
                "rds.us-east-1.amazonaws.com",
                "secretsmanager.us-east-1.amazonaws.com",
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
            StringEquals = { "kms:ViaService" = "secretsmanager.us-east-1.amazonaws.com" }
            StringNotEquals = { "aws:PrincipalArn" = [
              "arn:aws:iam::111122223333:role/insolvia-staging-migrator-role",
              "arn:aws:iam::111122223333:role/aws-service-role/rds.amazonaws.com/AWSServiceRoleForRDS",
            ] }
          }
        },
      ]
    })
    error_message = "The database key policy changed. Update this pin deliberately — re-read ADR 0030 § Sealed data and logs first."
  }

  # Stated on its own so a failure names the promise: no statement in the
  # policy ALLOWS Decrypt to any named principal — only root delegation,
  # fenced by the two denies.
  assert {
    condition = alltrue([
      for s in jsondecode(output.key_policy).Statement :
      s.Effect == "Deny" || s.Sid == "EnableIAMPolicies"
    ])
    error_message = "Only the root delegation may Allow on the database key."
  }

  assert {
    condition     = aws_kms_alias.database.name == "alias/insolvia-staging-database"
    error_message = "Naming: alias/[project]-[env]-[component]."
  }

  assert {
    condition     = jsondecode(aws_iam_role_policy.connect["api"].policy).Statement[0].Resource == "arn:aws:rds-db:us-east-1:111122223333:dbuser:cluster-ABCDEFGHIJ/api"
    error_message = "The API's role connects as the `api` Postgres role, and as nothing else."
  }

  assert {
    condition     = output.database_urls.worker == "postgresql://worker@insolvia-staging-database.cluster-x.us-east-1.rds.amazonaws.com:5432/insolvia?sslmode=require"
    error_message = "A service's DATABASE_URL names its own role, no password, TLS required."
  }

  assert {
    condition     = aws_rds_cluster.database.iam_database_authentication_enabled && aws_rds_cluster.database.storage_encrypted && aws_rds_cluster.database.master_username == "migrator"
    error_message = "IAM auth on, storage encrypted, master user is the migrator."
  }

  assert {
    condition     = aws_rds_cluster.database.serverlessv2_scaling_configuration[0].min_capacity == 0
    error_message = "ADR 0030: minimum capacity 0."
  }
}

run "refuses_another_major" {
  command = plan

  variables {
    engine_version = "16.4"
  }

  expect_failures = [var.engine_version]
}
