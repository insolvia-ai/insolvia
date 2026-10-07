# Pins the filing queue's two grants WHOLE (ADR 0024 PR 6: "approval is the
# only producer of a filing job"). Offline: the AWS provider is mocked, so
# `terraform test` needs no credentials and creates nothing. Run from the
# module directory:
#
#   terraform -chdir=infra/modules/filing_queue init -backend=false
#   terraform -chdir=infra/modules/filing_queue test
#
# shared-infra-plan.yml runs every module's tests/ on every PR.
#
# The API may SEND and nothing else; the filing worker may CONSUME and never
# send. A change to either is a change to who can create a filing, and must
# change this file.

mock_provider "aws" {
  mock_resource "aws_sqs_queue" {
    defaults = {
      arn = "arn:aws:sqs:us-east-1:111122223333:insolvia-staging-filing"
      url = "https://sqs.us-east-1.amazonaws.com/111122223333/insolvia-staging-filing"
    }
  }
}

variables {
  environment      = "staging"
  api_role_name    = "insolvia-staging-api-role"
  worker_role_name = "insolvia-staging-filing-role"
}

run "the_grants_are_pinned" {
  command = apply

  assert {
    condition = output.enqueue_policy == jsonencode({
      Version = "2012-10-17"
      Statement = [
        {
          Sid      = "FilingQueueSend"
          Effect   = "Allow"
          Action   = ["sqs:SendMessage"]
          Resource = "arn:aws:sqs:us-east-1:111122223333:insolvia-staging-filing"
        },
      ]
    })
    error_message = "The API's grant on the filing queue must be sqs:SendMessage alone."
  }

  assert {
    condition = output.consume_policy == jsonencode({
      Version = "2012-10-17"
      Statement = [
        {
          Sid    = "FilingQueueConsume"
          Effect = "Allow"
          Action = [
            "sqs:ReceiveMessage",
            "sqs:DeleteMessage",
            "sqs:ChangeMessageVisibility",
            "sqs:GetQueueAttributes",
          ]
          Resource = "arn:aws:sqs:us-east-1:111122223333:insolvia-staging-filing"
        },
      ]
    })
    error_message = "The filing worker consumes its queue and never sends to it."
  }

  assert {
    condition     = aws_iam_role_policy.api_enqueue[0].role == "insolvia-staging-api-role"
    error_message = "The send grant attaches to the API role."
  }

  assert {
    condition     = aws_iam_role_policy.worker_consume.role == "insolvia-staging-filing-role"
    error_message = "The consume grant attaches to the filing worker's role."
  }

  assert {
    condition     = aws_sqs_queue.filing.name == "insolvia-staging-filing" && aws_sqs_queue.filing_dlq.name == "insolvia-staging-filing-dlq"
    error_message = "insolvia-<env>-filing and its -dlq (insolvia-aws-naming)."
  }

  assert {
    condition     = aws_sqs_queue.filing.message_retention_seconds == 7200 && aws_sqs_queue.filing.sqs_managed_sse_enabled
    error_message = "Two approval lifetimes of retention, SSE-SQS."
  }
}

run "dev_has_no_api_grant" {
  command = apply

  variables {
    environment      = "dev-abc123"
    api_role_name    = null
    worker_role_name = "insolvia-dev-abc123-filing-role"
  }

  assert {
    condition     = length(aws_iam_role_policy.api_enqueue) == 0
    error_message = "Dev has no API role; the developer sends under their own credentials."
  }
}
