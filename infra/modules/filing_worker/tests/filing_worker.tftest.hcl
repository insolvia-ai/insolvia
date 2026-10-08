# Pins the filing worker's case-data grant WHOLE (ADR 0024: "a compromise of
# the worker role itself defeats the approval check, which is why that role
# is the narrowest in the account"). Offline: the AWS provider is mocked, so
# `terraform test` needs no credentials and creates nothing. Run from the
# module directory:
#
#   terraform -chdir=infra/modules/filing_worker init -backend=false
#   terraform -chdir=infra/modules/filing_worker test
#
# shared-infra-plan.yml runs every module's tests/ on every PR.
#
# A change to what the worker may read or write MUST change this file.

mock_provider "aws" {
  override_data {
    target = data.aws_caller_identity.current
    values = { account_id = "111122223333" }
  }
  override_data {
    target = data.aws_region.current
    values = { region = "us-east-1" }
  }
}

variables {
  environment                = "staging"
  insolvia_env               = "staging"
  ecr_repository_url         = "111122223333.dkr.ecr.us-east-1.amazonaws.com/insolvia-shared-filing"
  image_tag                  = "staging"
  worker_role_name           = "insolvia-staging-filing-role"
  worker_role_arn            = "arn:aws:iam::111122223333:role/insolvia-staging-filing-role"
  queue_arn                  = "arn:aws:sqs:us-east-1:111122223333:insolvia-staging-filing"
  dlq_name                   = "insolvia-staging-filing-dlq"
  case_table_arn             = "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-cases"
  case_table_name            = "insolvia-staging-cases"
  case_access_log_table_name = "insolvia-staging-case-access-log"
  case_kms_key_arn           = "arn:aws:kms:us-east-1:111122223333:key/case-key"
  case_document_bucket_arn   = "arn:aws:s3:::insolvia-staging-case-documents-us-east-1"
  case_document_bucket_name  = "insolvia-staging-case-documents-us-east-1"
  alarms_topic_arn           = "arn:aws:sns:us-east-1:111122223333:insolvia-staging-api-alarms"
}

run "the_worker_grant_is_pinned" {
  command = apply

  assert {
    condition = output.worker_policy == jsonencode({
      Version = "2012-10-17"
      Statement = [
        {
          Sid      = "FilingCaseRead"
          Effect   = "Allow"
          Action   = ["dynamodb:GetItem", "dynamodb:Query"]
          Resource = "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-cases"
          Condition = {
            Bool = { "aws:SecureTransport" = "true" }
          }
        },
        {
          Sid      = "FilingRecordWrite"
          Effect   = "Allow"
          Action   = ["dynamodb:PutItem", "dynamodb:UpdateItem"]
          Resource = "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-cases"
          Condition = {
            Bool = { "aws:SecureTransport" = "true" }
          }
        },
        {
          Sid      = "CaseKeyThroughDynamoDb"
          Effect   = "Allow"
          Action   = ["kms:Decrypt", "kms:GenerateDataKey", "kms:DescribeKey"]
          Resource = "arn:aws:kms:us-east-1:111122223333:key/case-key"
          Condition = {
            StringEquals = { "kms:ViaService" = "dynamodb.us-east-1.amazonaws.com" }
          }
        },
        {
          Sid      = "PacketRead"
          Effect   = "Allow"
          Action   = ["s3:GetObject"]
          Resource = "arn:aws:s3:::insolvia-staging-case-documents-us-east-1/cases/*/packets/*"
        },
        {
          Sid      = "FilingReceiptWrite"
          Effect   = "Allow"
          Action   = ["s3:PutObject"]
          Resource = "arn:aws:s3:::insolvia-staging-case-documents-us-east-1/cases/*/filings/*"
        },
        {
          Sid      = "CaseKeyThroughS3"
          Effect   = "Allow"
          Action   = ["kms:Decrypt", "kms:GenerateDataKey", "kms:DescribeKey"]
          Resource = "arn:aws:kms:us-east-1:111122223333:key/case-key"
          Condition = {
            StringEquals = { "kms:ViaService" = "s3.us-east-1.amazonaws.com" }
          }
        },
        {
          Sid      = "KillSwitchRead"
          Effect   = "Allow"
          Action   = ["ssm:GetParameter"]
          Resource = "arn:aws:ssm:us-east-1:111122223333:parameter/insolvia/staging/filing/submissions-enabled"
        },
      ]
    })
    error_message = "The filing worker's grant changed — this file must change with it, and the diff must say why."
  }

  assert {
    condition     = aws_ssm_parameter.kill_switch.value == "false"
    error_message = "The kill switch must be created OFF."
  }

  assert {
    condition     = aws_lambda_event_source_mapping.filing[0].batch_size == 1
    error_message = "One filing per invocation."
  }

  assert {
    condition     = aws_lambda_function.worker[0].environment[0].variables["INSOLVIA_ENV"] == "staging"
    error_message = "The Lambda runs as the deployed environment, never local."
  }

  assert {
    condition     = !contains(keys(aws_lambda_function.worker[0].environment[0].variables), "FAKE_CMECF_URL")
    error_message = "A deployed worker is never pointed at a fake court."
  }
}

run "dev_has_the_grants_and_no_lambda" {
  command = apply

  variables {
    environment        = "dev-0123456789ab"
    ecr_repository_url = null
    alarms_topic_arn   = null
  }

  assert {
    condition     = output.function_name == null
    error_message = "Dev has no Lambda: the local poller consumes the queue as the role."
  }

  assert {
    condition     = length(aws_lambda_event_source_mapping.filing) == 0
    error_message = "Dev has no event source mapping."
  }

  assert {
    condition     = aws_iam_role_policy.worker.role == "insolvia-staging-filing-role"
    error_message = "The grants attach in dev too, so a laptop run uses them."
  }
}

run "the_lambda_cannot_be_local" {
  command = plan

  variables {
    insolvia_env = "local"
  }

  expect_failures = [var.insolvia_env]
}
