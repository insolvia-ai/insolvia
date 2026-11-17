# Pins the vault's policy documents WHOLE (ADR 0024 PR 4: "unit tests pin
# the policy documents"). Offline: the AWS provider is mocked, and the two
# data sources the documents read are overridden, so `terraform test` needs
# no credentials and creates nothing. Run from the module directory:
#
#   terraform -chdir=infra/modules/filing_credentials init -backend=false
#   terraform -chdir=infra/modules/filing_credentials test
#
# shared-infra-plan.yml runs exactly that on every PR.
#
# A change to the key policy MUST change this file. That is the point: the
# deny that keeps Decrypt to the worker alone is a sentence a reviewer should
# have to read twice, never a side effect of a refactor.

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
  environment          = "staging"
  table_kms_key_arn    = "arn:aws:kms:us-east-1:111122223333:key/case-key"
  access_log_table_arn = "arn:aws:dynamodb:us-east-1:111122223333:table/insolvia-staging-case-access-log"
  api_role_name        = "insolvia-staging-api-role"
}

run "deployed_key_policy_is_pinned" {
  # apply, under the mocked provider: still offline, and it makes the
  # attached role policy's rendered document known so it can be asserted.
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
          Sid       = "DenyDecryptToAllButFilingWorker"
          Effect    = "Deny"
          Principal = { AWS = "*" }
          Action    = "kms:Decrypt"
          Resource  = "*"
          Condition = {
            StringNotEquals = { "aws:PrincipalArn" = "arn:aws:iam::111122223333:role/insolvia-staging-filing-role" }
          }
        },
        {
          Sid       = "DenyReEncryptAndGrantsToEveryone"
          Effect    = "Deny"
          Principal = { AWS = "*" }
          Action    = ["kms:ReEncryptFrom", "kms:ReEncryptTo", "kms:CreateGrant"]
          Resource  = "*"
        },
        {
          Sid       = "DenyOpenOutsideTheVaultContext"
          Effect    = "Deny"
          Principal = { AWS = "*" }
          Action    = "kms:Decrypt"
          Resource  = "*"
          Condition = {
            StringNotEquals = { "kms:EncryptionContext:purpose" = "filing-credential" }
          }
        },
        {
          Sid       = "AllowFilingWorkerOpen"
          Effect    = "Allow"
          Principal = { AWS = "arn:aws:iam::111122223333:role/insolvia-staging-filing-role" }
          Action    = ["kms:Decrypt", "kms:DescribeKey"]
          Resource  = "*"
          Condition = {
            StringEquals = { "kms:EncryptionContext:purpose" = "filing-credential" }
          }
        },
        {
          Sid       = "AllowApiSeal"
          Effect    = "Allow"
          Principal = { AWS = "arn:aws:iam::111122223333:role/insolvia-staging-api-role" }
          Action    = "kms:GenerateDataKey"
          Resource  = "*"
          Condition = {
            StringEquals = { "kms:EncryptionContext:purpose" = "filing-credential" }
          }
        },
      ]
    })
    error_message = "The vault key policy changed. Update this pin deliberately — and re-read ADR 0024's guardrail 3 first."
  }

  # Belt and braces on the one property that matters most, stated on its
  # own so a failure says WHICH promise broke: no Allow anywhere in the
  # policy names the API's role with Decrypt.
  assert {
    condition = alltrue([
      for s in jsondecode(output.key_policy).Statement :
      !(s.Effect == "Allow" && try(s.Principal.AWS, "") == "arn:aws:iam::111122223333:role/insolvia-staging-api-role" && contains(flatten([s.Action]), "kms:Decrypt"))
    ])
    error_message = "The API's role must never be allowed kms:Decrypt on the vault key."
  }

  assert {
    condition = output.worker_trust_policy == jsonencode({
      Version = "2012-10-17"
      Statement = [
        {
          Sid       = "FilingWorkerLambda"
          Effect    = "Allow"
          Principal = { Service = "lambda.amazonaws.com" }
          Action    = "sts:AssumeRole"
        },
      ]
    })
    error_message = "A deployed filing worker's role must trust Lambda and nothing else."
  }

  assert {
    condition     = aws_kms_alias.vault.name == "alias/insolvia-staging-filing-credentials"
    error_message = "The alias must match ci-trust's DenyFilingCredentialDecryption pattern and the core adapter's derivation."
  }

  assert {
    condition     = aws_dynamodb_table.credentials.name == "insolvia-staging-filing-credentials"
    error_message = "The table name is derived by insolvia_core.adapters.aws.filing_credentials from the case table's."
  }

  assert {
    condition     = jsondecode(aws_iam_role_policy.api[0].policy).Statement[1].Action == ["kms:GenerateDataKey"]
    error_message = "The API's vault-key grant is GenerateDataKey alone — seal, never open."
  }
}

run "dev_has_no_api_role_and_lets_the_developer_assume_the_worker" {
  command = plan

  variables {
    environment         = "dev-0123456789ab"
    api_role_name       = null
    worker_assumable_by = ["arn:aws:iam::111122223333:user/developer"]
  }

  assert {
    condition     = length([for s in jsondecode(output.key_policy).Statement : s if s.Sid == "AllowApiSeal"]) == 0
    error_message = "With no API role there is no seal statement — the developer seals through root delegation."
  }

  assert {
    condition = [
      for s in jsondecode(output.key_policy).Statement : s.Condition.StringNotEquals["aws:PrincipalArn"]
      if s.Sid == "DenyDecryptToAllButFilingWorker"
    ] == ["arn:aws:iam::111122223333:role/insolvia-dev-0123456789ab-filing-role"]
    error_message = "Dev's key policy must deny Decrypt to everyone but dev's own worker role — the developer included."
  }

  assert {
    condition = jsondecode(output.worker_trust_policy).Statement[1] == {
      Sid       = "DeveloperAssumesFilingWorkerInDev"
      Effect    = "Allow"
      Principal = { AWS = ["arn:aws:iam::111122223333:user/developer"] }
      Action    = "sts:AssumeRole"
    }
    error_message = "Dev's worker role trusts the developer, so the open can be proved by assuming it."
  }

  assert {
    condition     = length(aws_iam_role_policy.api) == 0
    error_message = "No API role, no API grant."
  }
}

run "staging_refuses_a_developer_in_the_worker_trust" {
  command = plan

  variables {
    worker_assumable_by = ["arn:aws:iam::111122223333:user/developer"]
  }

  expect_failures = [aws_iam_role.worker]
}
