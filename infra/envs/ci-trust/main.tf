# ── CI trust anchor ─────────────────────────────────────────────
# The account-wide GitHub-Actions trust anchor, extracted from `shared` into
# its own state so it is NEVER applied by CI. It owns three things and nothing
# else: the GitHub OIDC provider, the `insolvia-github-actions` deploy role,
# and that role's permissions policy.
#
# WHY ITS OWN ROOT: the role's policy carries an explicit self-deny
# (DenySelfPrivilegeEscalation) forbidding the role from editing its own
# permissions — so a `terraform apply` of this root run AS that role fails.
# Every change here must therefore be applied by a human with their own admin
# credentials. Making that a property of the directory (there is deliberately
# no ci-trust-*.yml workflow) beats burying it as a surprise inside `shared`:
# `shared` can now be applied by CI freely, and the human gate is exactly here.
#
# Apply with scripts/apply-ci-trust.sh (guarded credential + plan + confirm).
# See docs/runbooks/aws-bootstrap.md § "The ci-trust anchor".

locals {
  # Environment stays "shared" on purpose: these are account-wide shared
  # infra, and keeping the tag value stable made the extraction from `shared`
  # a zero-diff state move. The Terraform root is named "ci-trust"; the
  # resources are still shared/account-level.
  common_tags = {
    Project     = "insolvia"
    Environment = "shared"
    ManagedBy   = "terraform"
  }
}

# ── Trust-anchor extraction: adopt the existing live resources (transitional) ──
# The OIDC provider, deploy role, and its policy already EXIST in AWS — they were
# created while they lived in infra/envs/shared. This is a NEW root with EMPTY
# state, so a bare apply would try to CREATE them and fail ("EntityAlreadyExists"
# for the role, the provider URL already registered, etc.).
#
# These `import` blocks adopt the live resources into ci-trust's state on the
# first `scripts/apply-ci-trust.sh` run instead. The apply then reconciles them
# to this config — which updates the policy to the current (correct) event-
# source-mapping scoping — while `shared`'s matching `removed` blocks forget them
# from that root. AWS is never destroyed or recreated; only the managing state
# changes. The import IDs are the account's real identifiers (account 521762924626).
#
# One-time: once imported, these are no-ops and should be deleted together with
# shared's `removed` blocks. See docs/runbooks/aws-bootstrap.md § "The ci-trust anchor".
import {
  to = aws_iam_openid_connect_provider.github
  id = "arn:aws:iam::521762924626:oidc-provider/token.actions.githubusercontent.com"
}

import {
  to = aws_iam_role.github_actions
  id = "insolvia-github-actions"
}

import {
  to = aws_iam_role_policy.github_permissions
  id = "insolvia-github-actions:insolvia-deploy"
}

# ── GitHub Actions OIDC deploy role ─────────────────────────────
# This is a dedicated Insolvia AWS account, so the account-level GitHub OIDC
# provider does not exist yet — create it here. (There is exactly one such
# provider per account; if you later consolidate accounts, switch this back to
# a `data` source.) AWS validates the token against its own trusted CA store,
# but the API still requires at least one thumbprint.
resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
  thumbprint_list = [
    "6938fd4d98bab03faadb97b34396831e3780aea1",
    "1c58a3a8518e8759bf075b76b750d4f2df264fcd",
  ]
  tags = local.common_tags
}

data "aws_iam_policy_document" "github_assume" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    effect  = "Allow"

    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }

    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }

    # Accept BOTH subject forms. The org enforces immutable subject claims, so
    # tokens carry the numeric-id `sub` (github_immutable_sub_prefix); the
    # mutable `owner/name` form is kept too so this keeps working if immutable
    # enforcement is ever disabled. StringLike over a list is an OR.
    condition {
      test     = "StringLike"
      variable = "token.actions.githubusercontent.com:sub"
      values = [
        "${var.github_immutable_sub_prefix}:*",
        "repo:${var.github_repo}:*",
      ]
    }
  }
}

resource "aws_iam_role" "github_actions" {
  name               = "insolvia-github-actions"
  assume_role_policy = data.aws_iam_policy_document.github_assume.json
  tags               = local.common_tags
}

# Permissions the deploy pipeline needs: manage the static-hosting stack, read
# the shared zone/cert, and push build artifacts. Scoped to Insolvia resources.
data "aws_iam_policy_document" "github_permissions" {
  statement {
    sid = "TerraformState"
    actions = [
      "s3:ListBucket",
      "s3:GetObject",
      "s3:PutObject",
      "s3:DeleteObject",
    ]
    resources = [
      "arn:aws:s3:::insolvia-terraform-state",
      "arn:aws:s3:::insolvia-terraform-state/*",
    ]
  }

  statement {
    sid = "WebHostingBuckets"
    actions = [
      "s3:*",
    ]
    resources = [
      "arn:aws:s3:::insolvia-web-*",
      "arn:aws:s3:::insolvia-web-*/*",
    ]
  }

  statement {
    sid = "EdgeAndDns"
    actions = [
      "cloudfront:*",
      "route53:*",
      "acm:*",
    ]
    resources = ["*"]
  }

  # ── Marketing site (issues #43, #47) ───────────────────────────
  # The marketing stack (infra/envs/staging and infra/envs/prod) needs only
  # one grant the other statements don't cover: its assets bucket (does not
  # match the insolvia-web-* prefix). The prefix below is environment-agnostic,
  # so adding the staging instantiation needed no change here.
  # Its ECR repository, HTTP API, and access logs are
  # covered by the backend-API statements below (EcrAuthToken,
  # EcrRepositories, HttpApis, ApiAccessLogGroups — all insolvia-* scoped);
  # the Lambda, its log group, execution role, CloudFront, and Route53 by
  # ForwarderCompute / LambdaLogGroups / ServiceRoleManagement / EdgeAndDns.
  statement {
    sid     = "MarketingAssetsBucket"
    actions = ["s3:*"]
    resources = [
      "arn:aws:s3:::insolvia-marketing-*",
      "arn:aws:s3:::insolvia-marketing-*/*",
    ]
  }
  # ── end marketing site ─────────────────────────────────────────

  # READ-ONLY on the pipeline's own role, and deliberately so.
  #
  # This environment manages the OIDC provider, this role, and this policy —
  # the three things that authorize CI in the first place. Terraform must be
  # able to REFRESH them or no plan can complete (that is what these actions
  # buy). It must NOT be able to MODIFY them: iam:PutRolePolicy here would let
  # any change to this file grant the pipeline admin, applied by the pipeline
  # itself, with a code diff as the only control.
  #
  # Consequence, and it is intended: a change to the deploy role, its policy,
  # or the OIDC provider makes the CI apply fail with AccessDenied. Privilege
  # changes require a human running apply with their own credentials. Everything
  # else in `shared` — zone, cert, DNS, email — still applies from CI normally.
  statement {
    sid = "DeployRoleSelfRead"
    actions = [
      "iam:GetRole",
      "iam:PassRole",
      "iam:ListRolePolicies",
      "iam:GetRolePolicy",
      "iam:ListAttachedRolePolicies",
      "iam:ListRoleTags",
    ]
    resources = [aws_iam_role.github_actions.arn]
  }

  # `infra/envs/shared` MANAGES the OIDC provider, so every apply must refresh
  # it. Without this, a CI apply dies on:
  #   AccessDenied: ... not authorized to perform: iam:GetOpenIDConnectProvider
  # Read and tag only — deliberately NOT DeleteOpenIDConnectProvider. This
  # provider is what lets CI authenticate at all; a pipeline that can delete its
  # own trust anchor can lock everyone out with no way back in via CI.
  statement {
    sid = "OidcProviderRead"
    actions = [
      "iam:GetOpenIDConnectProvider",
      "iam:ListOpenIDConnectProviders",
      "iam:ListOpenIDConnectProviderTags",
    ]
    resources = [aws_iam_openid_connect_provider.github.arn]
  }

  # ── Email stack (issues 1.5–1.11) ──────────────────────────────
  # SES identity/DKIM/MAIL FROM and receipt rules are account-and-region scoped;
  # most SES actions do not support resource-level ARNs, so this is "*" by
  # necessity rather than by laziness.
  statement {
    sid       = "SimpleEmailService"
    actions   = ["ses:*"]
    resources = ["*"]
  }

  # The inbound-mail bucket does not match the insolvia-web-* prefix above.
  statement {
    sid     = "InboundMailBucket"
    actions = ["s3:*"]
    resources = [
      "arn:aws:s3:::insolvia-inbound-mail-*",
      "arn:aws:s3:::insolvia-inbound-mail-*/*",
    ]
  }

  # The mailer's private content bucket (S3 message manifests) does not match
  # the insolvia-web-* prefix either — see infra/modules/mailer.
  statement {
    sid     = "MailerContentBucket"
    actions = ["s3:*"]
    resources = [
      "arn:aws:s3:::insolvia-mailer-*",
      "arn:aws:s3:::insolvia-mailer-*/*",
    ]
  }

  statement {
    sid       = "ForwarderCompute"
    actions   = ["lambda:*"]
    resources = ["arn:aws:lambda:*:${data.aws_caller_identity.current.account_id}:function:insolvia-*"]
  }

  # Event source mappings (the mailer's SQS → sender/feedback Lambda wiring)
  # are NOT covered by ForwarderCompute above: those actions are not on the
  # function ARN. Two statements, and the split is the whole lesson — learned
  # the hard way across four AccessDenied's:
  #
  #   * CREATE carries a FunctionName → AWS populates the lambda:FunctionArn
  #     condition key, so it can be pinned to the insolvia-* functions the
  #     mapping may target. That's real defense-in-depth (the role can't wire a
  #     queue to some other account's function) and it works.
  #
  #   * Every OTHER call addresses the mapping by its server-assigned UUID —
  #     GetEventSourceMapping (incl. the poll while a delete completes), Update,
  #     Delete, and the tag calls the provider makes on refresh. These carry no
  #     function ARN, so a lambda:FunctionArn condition is simply absent and
  #     DENIES them (that failure retired the one-statement shape).
  #
  #     The trap that cost the extra round-trip: scoping them by RESOURCE to
  #     event-source-mapping:* ALSO fails. Despite what the AWS Service
  #     Authorization Reference lists (an `eventSourceMapping` resource type on
  #     these actions), IAM evaluates lambda:GetEventSourceMapping at runtime
  #     against `*`, not the mapping ARN — the deny reads literally
  #     "...on resource: * because no identity-based policy allows...", so a
  #     mapping-ARN-scoped grant never matches. They MUST be granted on "*".
  #
  #     "*" is not a real widening here: these actions are only valid on event
  #     source mappings (you cannot GetEventSourceMapping a function), and this
  #     is a dedicated single-tenant Insolvia account whose only mappings are
  #     the mailer's — so "*" and event-source-mapping:* confer identical
  #     effective access. The difference is only that "*" is the one IAM honors.
  #     TagResource/UntagResource ride along so adding `tags` to the mailer's
  #     mappings later needs no fresh human-gated ci-trust apply.
  statement {
    sid       = "EventSourceMappingCreate"
    actions   = ["lambda:CreateEventSourceMapping"]
    resources = ["*"]

    condition {
      test     = "ArnLike"
      variable = "lambda:FunctionArn"
      values   = ["arn:aws:lambda:*:${data.aws_caller_identity.current.account_id}:function:insolvia-*"]
    }
  }

  statement {
    sid = "EventSourceMappingManage"
    actions = [
      "lambda:GetEventSourceMapping",
      "lambda:UpdateEventSourceMapping",
      "lambda:DeleteEventSourceMapping",
      "lambda:ListTags",
      "lambda:TagResource",
      "lambda:UntagResource",
    ]
    resources = ["*"]
  }

  statement {
    sid       = "ForwarderQueues"
    actions   = ["sqs:*"]
    resources = ["arn:aws:sqs:*:${data.aws_caller_identity.current.account_id}:insolvia-*"]
  }

  statement {
    sid       = "AlertTopics"
    actions   = ["sns:*"]
    resources = ["arn:aws:sns:*:${data.aws_caller_identity.current.account_id}:insolvia-*"]
  }

  # CloudWatch alarm APIs do not support resource-level permissions.
  statement {
    sid = "Alarms"
    actions = [
      "cloudwatch:PutMetricAlarm",
      "cloudwatch:DeleteAlarms",
      "cloudwatch:DescribeAlarms",
      "cloudwatch:ListTagsForResource",
      "cloudwatch:TagResource",
      "cloudwatch:UntagResource",
    ]
    resources = ["*"]
  }

  statement {
    sid       = "LambdaLogGroups"
    actions   = ["logs:*"]
    resources = ["arn:aws:logs:*:${data.aws_caller_identity.current.account_id}:log-group:/aws/lambda/insolvia-*"]
  }

  # ── Backend API stack (#62, #63, #69, #70) ─────────────────────
  # The API deploy builds a Docker image, pushes it to insolvia-api-<env>,
  # applies the env stacks, and points the Lambda at the new image. Most of
  # what it needs is already granted above: Lambda create/update on
  # function:insolvia-* (ForwarderCompute), the alarm SNS topic (AlertTopics),
  # CloudWatch alarm management (Alarms), Lambda log groups (LambdaLogGroups),
  # SSM under /insolvia/* (Parameters), Route53/ACM for the custom domain
  # (EdgeAndDns), and the execution role (ServiceRoleManagement, whose
  # AttachRolePolicy condition already allows exactly the
  # AWSLambdaBasicExecutionRole the API role attaches). The four statements
  # below are only what was missing.

  # Docker login. GetAuthorizationToken has no resource-level support and is
  # evaluated against "*"; the token alone grants nothing — every push and
  # pull is still checked against the repository statement below.
  statement {
    sid       = "EcrAuthToken"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid       = "EcrRepositories"
    actions   = ["ecr:*"]
    resources = ["arn:aws:ecr:*:${data.aws_caller_identity.current.account_id}:repository/insolvia-*"]
  }

  # Deliberately NOT dynamodb:* despite the service:* style above: the
  # waitlist table holds signup PII, and per docs/adr/0001 the API's execution
  # role is the only application principal that touches rows. The deploy role
  # manages tables, never their contents — control-plane actions only, no
  # PutItem/GetItem/Query/Scan.
  statement {
    sid = "WaitlistTableManagement"
    actions = [
      "dynamodb:CreateTable",
      "dynamodb:DeleteTable",
      "dynamodb:DescribeTable",
      "dynamodb:UpdateTable",
      "dynamodb:DescribeContinuousBackups",
      "dynamodb:UpdateContinuousBackups",
      "dynamodb:DescribeTimeToLive",
      "dynamodb:UpdateTimeToLive",
      "dynamodb:TagResource",
      "dynamodb:UntagResource",
      "dynamodb:ListTagsOfResource",
    ]
    resources = ["arn:aws:dynamodb:*:${data.aws_caller_identity.current.account_id}:table/insolvia-*"]
  }

  # API Gateway v2 IAM is path-based on generated API ids — there is no
  # insolvia-* name scoping like lambda/sqs/sns above. Constrained instead to
  # the resource paths Terraform manages (APIs, custom domains, tags); this
  # dedicated account runs nothing but Insolvia.
  statement {
    sid     = "HttpApis"
    actions = ["apigateway:*"]
    resources = [
      "arn:aws:apigateway:*::/apis",
      "arn:aws:apigateway:*::/apis/*",
      "arn:aws:apigateway:*::/domainnames",
      "arn:aws:apigateway:*::/domainnames/*",
      "arn:aws:apigateway:*::/tags/*",
    ]
  }

  # HTTP API access logs live under /aws/apigateway/…, which the
  # /aws/lambda/insolvia-* statement above does not match.
  statement {
    sid       = "ApiAccessLogGroups"
    actions   = ["logs:*"]
    resources = ["arn:aws:logs:*:${data.aws_caller_identity.current.account_id}:log-group:/aws/apigateway/insolvia-*"]
  }

  # Enabling access logging on an API GW v2 stage makes API Gateway register a
  # CloudWatch Logs *log delivery* on the CALLER's permissions, and the
  # delivery APIs are account-level — evaluated against "*", so the log-group-
  # scoped statement above never matches them (same family as EnumerationApis
  # below). Without this, CreateStage fails with "Insufficient permissions to
  # enable logging … not authorized to perform: logs:CreateLogDelivery" —
  # found by the first staging apply; validate can't see it.
  statement {
    sid = "LogDeliveries"
    actions = [
      "logs:CreateLogDelivery",
      "logs:GetLogDelivery",
      "logs:UpdateLogDelivery",
      "logs:DeleteLogDelivery",
      "logs:ListLogDeliveries",
      "logs:PutResourcePolicy",
      "logs:DescribeResourcePolicies",
    ]
    resources = ["*"]
  }

  # The account's FIRST API GW custom domain makes API Gateway create its
  # service-linked role (AWSServiceRoleForAPIGateway) on the caller's
  # permissions; without this, CreateDomainName fails with "Caller does not
  # have permissions to create a Service Linked Role" — also found by the
  # first staging apply. Locked to exactly that SLR three ways: the action,
  # the aws-service-role ARN path, and the service-name condition. Once the
  # role exists this statement is dormant (SLRs are one-per-account).
  statement {
    sid     = "ApiGatewayServiceLinkedRole"
    actions = ["iam:CreateServiceLinkedRole"]
    resources = [
      "arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/aws-service-role/ops.apigateway.amazonaws.com/AWSServiceRoleForAPIGateway",
    ]

    condition {
      test     = "StringLike"
      variable = "iam:AWSServiceName"
      values   = ["ops.apigateway.amazonaws.com"]
    }
  }
  # ── end backend API stack ──────────────────────────────────────

  # ── Auth stack (#65) ───────────────────────────────────────────
  # The env stacks each manage a Cognito user pool, its hosted domain, and
  # two app clients. Everything that CAN be resource-scoped is: pool ARNs are
  # arn:…:userpool/<opaque-id> — Cognito offers no name-based ARN shape like
  # lambda/sqs/sns's insolvia-* prefix, so userpool/* is the tightest scope
  # the service supports (and this dedicated account runs nothing but
  # Insolvia). Clients and domains are sub-resources of the pool and are
  # authorized against the pool ARN, so this one statement covers them.
  statement {
    sid       = "CognitoUserPools"
    actions   = ["cognito-idp:*"]
    resources = ["arn:aws:cognito-idp:*:${data.aws_caller_identity.current.account_id}:userpool/*"]
  }

  # The handful of cognito-idp actions with NO resource type, evaluated
  # against "*" (same family as EcrAuthToken/EnumerationApis above): a
  # userpool/*-scoped statement never matches them, so the very first CI
  # apply would die on CreateUserPool. DescribeUserPoolDomain's request
  # carries only the domain prefix — no pool ARN to scope against — and
  # Terraform calls it on every refresh of aws_cognito_user_pool_domain.
  # ListUserPools is a read-only listing (names and ids, never contents).
  statement {
    sid = "CognitoAccountApis"
    actions = [
      "cognito-idp:CreateUserPool",
      "cognito-idp:DescribeUserPoolDomain",
      "cognito-idp:ListUserPools",
    ]
    resources = ["*"]
  }
  # ── end auth stack ─────────────────────────────────────────────

  # The forward-to destination lives here as a SecureString. Terraform creates
  # the parameter but never owns its value (lifecycle ignore_changes).
  statement {
    sid       = "Parameters"
    actions   = ["ssm:*"]
    resources = ["arn:aws:ssm:*:${data.aws_caller_identity.current.account_id}:parameter/insolvia/*"]
  }

  # ssm:* above is NOT sufficient for a SecureString. The value is encrypted
  # under the AWS-managed key alias/aws/ssm, whose key policy delegates to IAM
  # — so creating one (api_service's unsubscribe-secret, #80) and reading one
  # back (`get-parameters-by-path --with-decryption` in api-<env>.yml) both
  # need these KMS actions in *this* policy, or the apply fails with an
  # AccessDenied naming a key rather than a parameter.
  #
  # Scoped by condition rather than by resource: the AWS-managed key's ID is
  # not knowable here without a data lookup, and kms:ViaService is the
  # stronger control anyway — it makes this grant usable ONLY when SSM is the
  # one calling KMS. It cannot be turned into "decrypt anything in the
  # account".
  statement {
    sid = "ParameterEncryption"
    actions = [
      "kms:Decrypt",
      "kms:Encrypt",
      "kms:GenerateDataKey",
      "kms:DescribeKey",
    ]
    resources = ["*"]

    condition {
      test     = "StringEquals"
      variable = "kms:ViaService"
      values   = ["ssm.${var.aws_region}.amazonaws.com"]
    }
  }

  # Enumeration APIs that do NOT support resource-level permissions. AWS
  # evaluates these against `arn:aws:<service>:<region>:<account>:*`, so a
  # prefix-scoped resource never matches and the call is denied no matter how
  # the parameter itself is scoped. Terraform calls them on every refresh —
  # ssm:DescribeParameters is what broke the first email apply:
  #
  #   AccessDeniedException: ... not authorized to perform:
  #   ssm:DescribeParameters on resource: arn:aws:ssm:us-east-1:...:*
  #
  # All are read-only listings that expose names and metadata, never contents:
  # reading a SecureString's value still requires ssm:GetParameter, which stays
  # scoped to parameter/insolvia/* above.
  statement {
    sid = "EnumerationApis"
    actions = [
      "ssm:DescribeParameters",
      "logs:DescribeLogGroups",
      "sqs:ListQueues",
      "sns:ListTopics",
      "lambda:ListFunctions",
      "lambda:GetAccountSettings",
    ]
    resources = ["*"]
  }

  # The forwarder Lambda needs its own execution role, so the pipeline must be
  # able to create roles — scoped to the insolvia-* name prefix.
  statement {
    sid = "ServiceRoleManagement"
    actions = [
      "iam:CreateRole",
      "iam:DeleteRole",
      "iam:GetRole",
      "iam:PassRole",
      "iam:TagRole",
      "iam:UntagRole",
      "iam:PutRolePolicy",
      "iam:GetRolePolicy",
      "iam:DeleteRolePolicy",
      "iam:ListRolePolicies",
      "iam:ListAttachedRolePolicies",
      "iam:ListInstanceProfilesForRole",
    ]
    resources = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/insolvia-*"]
  }

  # ── The guard that makes all of the above safe ─────────────────
  # An EXPLICIT deny on mutating the pipeline's own identity. Explicit deny
  # beats every allow in IAM, including any added later.
  #
  # This is not belt-and-braces. The role is named `insolvia-github-actions`,
  # which MATCHES the `role/insolvia-*` resource pattern in
  # ServiceRoleManagement above — so without this statement the pipeline would
  # hold iam:PutRolePolicy and iam:DeleteRole over itself, and any change to
  # this file could grant it admin, applied by itself, reviewed only as a diff.
  #
  # Relying on the role's name NOT matching a prefix is not a control: it is
  # invisible, undocumented, and silently undone by a rename. This is the
  # control. Verified with `aws iam simulate-custom-policy`.
  statement {
    sid    = "DenySelfPrivilegeEscalation"
    effect = "Deny"
    actions = [
      "iam:PutRolePolicy",
      "iam:DeleteRolePolicy",
      "iam:AttachRolePolicy",
      "iam:DetachRolePolicy",
      "iam:UpdateAssumeRolePolicy",
      "iam:UpdateRole",
      "iam:DeleteRole",
      "iam:CreateRole",
      "iam:TagRole",
      "iam:UntagRole",
    ]
    resources = [aws_iam_role.github_actions.arn]
  }

  # Likewise for the trust anchor: CI may read it, never change or remove it.
  statement {
    sid    = "DenyTrustAnchorMutation"
    effect = "Deny"
    actions = [
      "iam:DeleteOpenIDConnectProvider",
      "iam:UpdateOpenIDConnectProviderThumbprint",
      "iam:AddClientIDToOpenIDConnectProvider",
      "iam:RemoveClientIDFromOpenIDConnectProvider",
    ]
    resources = [aws_iam_openid_connect_provider.github.arn]
  }

  # Attaching AWS-managed policies is constrained to the single policy the
  # forwarder actually uses. Without this condition the pipeline could attach
  # AdministratorAccess to a role it creates and then pass it to a Lambda —
  # a straightforward privilege-escalation path out of a scoped deploy role.
  statement {
    sid = "ServiceRolePolicyAttachment"
    actions = [
      "iam:AttachRolePolicy",
      "iam:DetachRolePolicy",
    ]
    resources = ["arn:aws:iam::${data.aws_caller_identity.current.account_id}:role/insolvia-*"]

    condition {
      test     = "ArnEquals"
      variable = "iam:PolicyARN"
      values   = ["arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"]
    }
  }
}

data "aws_caller_identity" "current" {}

resource "aws_iam_role_policy" "github_permissions" {
  name   = "insolvia-deploy"
  role   = aws_iam_role.github_actions.id
  policy = data.aws_iam_policy_document.github_permissions.json
}

