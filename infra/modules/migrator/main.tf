# ── The migrator (ADR 0028 § Runtime shape, plan 18.22) ─────────
# backend/src/Insolvia.Migrator as a one-shot image Lambda in the VPC: it
# applies the EF Core migrations, (re)creates the per-service Postgres roles
# with rds_iam and re-asserts every table grant — and, on staging, converges
# the environment on seeds/staging.json. It exists because the database is
# private: a GitHub runner cannot reach Aurora, so CI invokes THIS before
# shifting any `live` alias (the deploy workflows' "Migrate" leg).
#
# What one invocation does is its JSON payload (MigratorCommand.cs):
#   {}                          migrate; then seed when INSOLVIA_SEED=true
#   {"command": "seed"}         converge on seeds/<env>.json (refused for
#                               production before anything connects)
#   {"command": "seed", "check": true}   report what is missing, write nothing
#   {"command": "purge-pre-client"}      dry run (ADR 0022)
# A refusal or an exception is a FunctionError, which is what CI checks.
#
# ── Who it is ───────────────────────────────────────────────────
# The ONE principal that reads the RDS-managed master secret (only it can
# create roles), and the one Lambda with no rds-db:connect: it signs in as the
# master over TLS and takes `SET ROLE migrator` for the seed, whose grants are
# INSERT/SELECT plus the UPDATEs a store write needs and never DELETE
# (Grants.cs). The deploy role is explicitly denied GetSecretValue on every
# `rds!cluster-*` secret by insolvia-services' ci-trust; nothing else holds it.
#
# Seeding (var.seed, staging only) adds what the loader touches outside the
# database — the same reach the old staging seed role had, now on a role
# only this Lambda can assume: the firm pool's test accounts (AdminGetUser,
# AdminCreateUser with SUPPRESS, AdminSetUserPassword), server-side copies
# of the fixture documents out of the shared fixture bucket into this
# environment's case-documents bucket, and the case key for those objects
# (through S3) and for the tax-id envelopes it seals (the encryption-context
# fence modules/case_store uses). Production gets none of it: its migrator
# migrates, and the loader refuses production regardless.
#
# ── Bootstrap order (the FIRST apply in a fresh environment) ────
# The image-before-apply deadlock every image Lambda here has: run
#   scripts/bootstrap-ecr-images.sh <env> migrator
# (insolvia-shared-migrator-dotnet:<env>) before the apply that adds this
# module. Later deploys are the workflows pushing an image and calling
# update-function-code; Terraform ignores the drift.

data "aws_region" "current" {}

locals {
  # insolvia-<env>-migrator — the component is the job it does.
  name = "${var.project}-${var.environment}-migrator"

  ssm_prefix = "/${var.project}/${var.environment}/migrator"

  tax_id_purpose = "debtor-tax-id"
}

data "aws_iam_policy_document" "lambda_trust" {
  statement {
    effect  = "Allow"
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["lambda.amazonaws.com"]
    }
  }
}

resource "aws_iam_role" "migrator" {
  name               = "${local.name}-role"
  assume_role_policy = data.aws_iam_policy_document.lambda_trust.json
  tags               = var.tags
}

resource "aws_iam_role_policy_attachment" "migrator_basic" {
  role       = aws_iam_role.migrator.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# The VPC's network interfaces; no rds-db:connect (see the header).
module "vpc_access" {
  source = "../vpc_lambda_access"

  name      = local.name
  role_name = aws_iam_role.migrator.name
}

resource "aws_iam_role_policy" "migrator" {
  name = "${local.name}-access"
  role = aws_iam_role.migrator.id
  policy = jsonencode({
    Version = "2012-10-17"
    # Seed statements only where var.seed (staging); production's migrator
    # holds the master-secret read and nothing else.
    Statement = [for s in [
      {
        # The master credential, read per run (it rotates). The secret is
        # under the AWS-managed aws/secretsmanager key (modules/aurora says
        # why), whose policy admits any principal of the account calling
        # through Secrets Manager — so no KMS statement is needed here.
        Sid      = "MasterSecretRead"
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = var.master_secret_arn
      },
      {
        Sid    = "SeedAccounts"
        Effect = "Allow"
        Action = [
          "cognito-idp:AdminGetUser",
          "cognito-idp:AdminCreateUser",
          "cognito-idp:AdminSetUserPassword",
        ]
        Resource = var.user_pool_arn
      },
      {
        Sid      = "SeedFixtureRead"
        Effect   = "Allow"
        Action   = ["s3:GetObject"]
        Resource = "${var.fixture_bucket_arn}/*"
      },
      {
        # The fixture case's documents land under cases/<case_id>/…, the
        # API's own layout; tagging because a copy carries the source's tags.
        Sid      = "SeedDocumentWrite"
        Effect   = "Allow"
        Action   = ["s3:PutObject", "s3:GetObject", "s3:PutObjectTagging"]
        Resource = "${var.case_document_bucket_arn}/cases/*"
      },
      {
        Sid      = "SeedDocumentKeyThroughS3"
        Effect   = "Allow"
        Action   = ["kms:Decrypt", "kms:GenerateDataKey", "kms:DescribeKey"]
        Resource = var.case_kms_key_arn
        Condition = {
          StringEquals = { "kms:ViaService" = "s3.${data.aws_region.current.region}.amazonaws.com" }
        }
      },
      {
        # Seal only: the seeder enters the fixture debtors' tax ids and never
        # reads one back. modules/case_store's TaxIdKeyUse fence.
        Sid      = "SeedTaxIdSeal"
        Effect   = "Allow"
        Action   = ["kms:GenerateDataKey"]
        Resource = var.case_kms_key_arn
        Condition = {
          StringEquals = { "kms:EncryptionContext:purpose" = local.tax_id_purpose }
        }
      },
    ] : s if var.seed || s.Sid == "MasterSecretRead"]
  })

  lifecycle {
    precondition {
      condition     = !var.seed || (var.user_pool_arn != null && var.fixture_bucket_arn != null && var.case_document_bucket_arn != null && var.case_kms_key_arn != null)
      error_message = "A seeding migrator needs user_pool_arn, fixture_bucket_arn, case_document_bucket_arn and case_kms_key_arn."
    }
    precondition {
      condition     = !var.seed || var.insolvia_env != "production"
      error_message = "Production is never seeded (seeds/README.md); the loader refuses it too."
    }
  }
}

resource "aws_lambda_function" "migrator" {
  function_name = local.name
  role          = aws_iam_role.migrator.arn
  package_type  = "Image"
  image_uri     = "${var.ecr_repository_url}:${var.image_tag}"
  # A migration may rewrite a table and Aurora at minimum capacity 0 takes
  # ~15 s to resume; the seed copies documents. Lambda's maximum, and the
  # invoking step's read timeout matches it.
  timeout     = 900
  memory_size = 1024

  # No `publish`, no alias: a one-shot invoked by CI on $LATEST right after
  # its code is updated — there is no traffic to protect.

  vpc_config {
    subnet_ids         = var.vpc_subnet_ids
    security_group_ids = var.vpc_security_group_ids
  }

  environment {
    variables = {
      INSOLVIA_ENV = var.insolvia_env
    }
  }

  # The deploy workflow owns both, as for every service Lambda here: it
  # pushes the image and derives the environment from /insolvia/<env>/migrator
  # and /insolvia/<env>/db (.github/scripts/lambda-environment.sh), overlaying
  # staging's E2E_TEST_USER_PASSWORD from the GitHub environment. The block
  # above is only the first-apply seed.
  lifecycle { ignore_changes = [image_uri, environment] }

  tags = var.tags
  depends_on = [
    aws_iam_role_policy_attachment.migrator_basic,
    aws_iam_role_policy.migrator,
    module.vpc_access,
  ]
}

resource "aws_cloudwatch_log_group" "migrator" {
  name              = "/aws/lambda/${aws_lambda_function.migrator.function_name}"
  retention_in_days = 30
  tags              = var.tags
}

# ── Configuration namespace ─────────────────────────────────────
# /insolvia/<env>/migrator/<key>, the services' convention. The seed's
# cross-module values (pool, buckets, case key) are written by the env root.
resource "aws_ssm_parameter" "config" {
  for_each = merge(
    { "insolvia-env" = var.insolvia_env },
    var.seed ? { "insolvia-seed" = "true" } : {},
  )

  name  = "${local.ssm_prefix}/${each.key}"
  type  = "String"
  value = each.value
  tags  = var.tags
}

# A failed migration fails the deploy that invoked it, loudly, in CI — so no
# alarm on Errors: the run IS the alert. The log group above keeps the record.
