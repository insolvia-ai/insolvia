# Pins the API's actions on the firm table. Offline: the AWS provider is
# mocked, so `terraform test` needs no credentials and creates nothing. Run
# from the module directory:
#
#   terraform -chdir=infra/modules/firm_store init -backend=false
#   terraform -chdir=infra/modules/firm_store test
#
# shared-infra-plan.yml runs every module's tests/ on every PR.
#
# dynamodb:ConditionCheckItem is load-bearing and easy to lose: linking a
# debtor to a client, and opening a case for one, check the client's row HERE
# from inside the case table's transaction (insolvia_core.client_merge — the
# merge race). Without it every link and every case open is AccessDenied.
# Scan stays absent: only the admin service lists every firm.

mock_provider "aws" {
  mock_resource "aws_dynamodb_table" {
    defaults = {
      arn = "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-firms"
    }
  }
}

variables {
  environment   = "staging"
  aws_region    = "us-east-1"
  kms_key_arn   = "arn:aws:kms:us-east-1:111122223333:key/00000000-0000-4000-8000-000000000000"
  api_role_name = "insolvia-staging-api-role"
}

run "the_api_grant_is_pinned" {
  command = apply

  assert {
    condition = jsondecode(aws_iam_role_policy.api_firm_access[0].policy).Statement[0].Action == [
      "dynamodb:GetItem",
      "dynamodb:Query",
      "dynamodb:PutItem",
      "dynamodb:UpdateItem",
      "dynamodb:DeleteItem",
      "dynamodb:BatchGetItem",
      "dynamodb:ConditionCheckItem",
    ]
    error_message = "The API's actions on the firm table changed — ConditionCheckItem is the client-row condition a link and a case open carry, and Scan must stay absent."
  }

  assert {
    condition = jsondecode(aws_iam_role_policy.api_firm_access[0].policy).Statement[0].Resource == [
      "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-firms",
      "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-firms/index/*",
    ]
    error_message = "The API's grant is on the firm table and its indexes, nothing else."
  }

  assert {
    condition     = aws_iam_role_policy.api_firm_access[0].role == "insolvia-staging-api-role"
    error_message = "The firm-table grant attaches to the API role."
  }
}
