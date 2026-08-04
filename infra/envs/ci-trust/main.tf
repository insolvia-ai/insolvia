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

  # ── Case data store (milestone 5) ──────────────────────────────
  # First customer-managed keys in the account. The case store and the case
  # document bucket hold GLBA-scope data (SSNs, full financials), and business
  # plan §10 commits to encryption at rest under a key we control rather than
  # the AWS-managed default.
  #
  # These sit on "*" rather than an ARN fence, and that is deliberate rather
  # than lazy. KMS keys are identified by generated UUIDs, so there is no
  # insolvia-* name pattern to scope to, and kms:CreateKey authorizes against
  # no resource at all. That leaves tags as the only available fence, and tags
  # are the wrong tool here for two independent reasons:
  #
  #   - KMS tag changes take EFFECT ON AUTHORIZATION LAZILY (AWS documents up
  #     to five minutes, and advises against tag-based access control for
  #     critical resources). Terraform calls EnableKeyRotation and PutKeyPolicy
  #     milliseconds after CreateKey, so a tag fence produces an intermittent
  #     AccessDenied on first apply — and every one of those costs a human
  #     ci-trust apply to diagnose.
  #   - A key whose Project tag is ever missing becomes permanently
  #     unmanageable: the create succeeds, the key lands in state, and every
  #     later call including the refresh read is denied. Recovering needs a
  #     human apply AND state surgery.
  #
  # The real control is DenyCaseDataDecryption below, which is an explicit
  # deny and therefore beats every allow here. Scope comes from that, not from
  # the resource list.
  statement {
    sid = "EncryptionKeyCreate"
    actions = [
      "kms:CreateKey",
      "kms:TagResource",
    ]
    resources = ["*"]
  }

  # Control-plane only — no Decrypt, GenerateDataKey or ReEncrypt, matching
  # the waitlist table's split above: per docs/adr/0001 the API's execution
  # role is the only application principal that touches case data.
  #
  # Note carefully what this does and does not buy. kms:PutKeyPolicy is here
  # because Terraform owns the key policy, and a KMS key policy authorizes a
  # principal ON ITS OWN — unlike S3, no identity-policy allow is also
  # required. DenyCaseDataDecryption below stops the pipeline using that to
  # read case data ITSELF, since an explicit deny beats a key-policy allow.
  #
  # It does not make the key policy harmless, and pretending otherwise would
  # be worse than saying so:
  #
  #   - A deny only constrains the principal it names. This role could write a
  #     key policy granting kms:Decrypt to a principal in ANOTHER account,
  #     which is evaluated against that principal's policies, not this one's.
  #   - ServiceRoleManagement already grants iam:PutRolePolicy and iam:PassRole
  #     over role/insolvia-*, so a new role with case-data access and a Lambda
  #     to run it is reachable too.
  #
  # Both are inherent to a deploy role that provisions the store, and neither
  # is fixable in an identity policy. The controls that actually cover them are
  # the trail (every PutKeyPolicy is recorded) and PR review of the Terraform
  # diff that would have to carry the change.
  #
  # Availability reach is also real and accepted: with no resource fence,
  # ScheduleKeyDeletion and DisableKey apply to any customer-managed key in
  # the account, including one a human admin creates to hold something back
  # from CI.
  statement {
    sid = "EncryptionKeyManagement"
    actions = [
      "kms:DescribeKey",
      "kms:GetKeyPolicy",
      "kms:PutKeyPolicy",
      "kms:GetKeyRotationStatus",
      "kms:EnableKeyRotation",
      "kms:DisableKeyRotation",
      "kms:ListResourceTags",
      "kms:UntagResource",
      "kms:EnableKey",
      "kms:DisableKey",
      "kms:UpdateKeyDescription",
      "kms:ScheduleKeyDeletion",
      "kms:CancelKeyDeletion",
    ]
    resources = ["*"]
  }

  # DynamoDB creates a grant on the caller's behalf when a table is configured
  # with a customer-managed key, so CreateTable fails without this. Fenced by
  # kms:GrantIsForAWSResource, which is what keeps it from being a general
  # escalation verb: the grantee can only ever be an AWS service, never a
  # principal of our choosing.
  #
  # S3 is NOT in the same boat — SSE-KMS uses no grants, and S3 calls
  # GenerateDataKey/Decrypt as the requester. One consequence worth knowing
  # before 8.6 designs upload: the pipeline cannot read or write objects in an
  # SSE-KMS case bucket at all, because DenyCaseDataDecryption fires with
  # kms:ViaService = s3. That is the desired posture — case documents move
  # through the API, never through CI — but it does mean an aws_s3_object
  # targeting insolvia-case-* would fail, by design rather than by accident.
  statement {
    sid = "EncryptionKeyGrants"
    actions = [
      "kms:CreateGrant",
      "kms:ListGrants",
      "kms:RevokeGrant",
    ]
    resources = ["*"]

    condition {
      test     = "Bool"
      variable = "kms:GrantIsForAWSResource"
      values   = ["true"]
    }
  }

  # An `aws_cloudtrail` with kms_key_id set makes the CALLER prove it can use
  # the key, so CreateTrail needs GenerateDataKey even though CloudTrail does
  # the encrypting. Carving CloudTrail out of DenyCaseDataDecryption below is
  # necessary but NOT sufficient — removing a deny never creates an allow, and
  # `aws iam simulate-principal-policy` returned implicitDeny for exactly this
  # call until this statement existed.
  #
  # Fenced by kms:ViaService, which AWS sets when a service calls on the
  # caller's behalf and a caller cannot supply itself — so this grants nothing
  # a direct Decrypt could reach. Note 8.2 can also skip it entirely:
  # encrypting the trail's destination BUCKET under the same key needs no KMS
  # grant here at all, because S3 then encrypts as its own principal.
  statement {
    sid = "TrailLogEncryption"
    actions = [
      "kms:GenerateDataKey",
      "kms:DescribeKey",
    ]
    resources = ["*"]

    condition {
      test     = "StringEquals"
      variable = "kms:ViaService"
      values   = ["cloudtrail.${var.aws_region}.amazonaws.com"]
    }
  }

  # Lambda encrypts every function's code package and environment variables,
  # and when no CMK is set it uses the AWS-MANAGED key `alias/aws/lambda`. It
  # asks the CALLER for the data key, so `aws lambda update-function-code` and
  # `update-function-configuration` both need this — which means every service
  # deploy does. Without it the deploy fails with "KMS access is denied ... not
  # authorized to perform: kms:GenerateDataKey", naming a key nobody in this
  # repo created and cannot find, because AWS owns it.
  #
  # Paired with the carve-out in DenyCaseDataDecryption below. Neither works
  # alone: an allow is still overridden by the deny, and a hole in the deny
  # without an allow leaves the implicit deny in place. This is the same trap
  # the CloudTrail pair documents, hit a second time.
  #
  # kms:Decrypt is included and is fenced by the same ViaService condition, so
  # it authorizes only what LAMBDA decrypts on this role's behalf — function
  # code and environment variables. It cannot reach case data: DynamoDB and S3
  # decrypt as their own service principals, so those calls carry
  # `kms:ViaService = dynamodb…`/`s3…` and the deny below still bites.
  statement {
    sid = "LambdaCodeAndEnvEncryption"
    actions = [
      "kms:GenerateDataKey",
      "kms:Encrypt",
      "kms:Decrypt",
      "kms:DescribeKey",
    ]
    resources = ["*"]

    condition {
      test     = "StringEquals"
      variable = "kms:ViaService"
      values   = ["lambda.${var.aws_region}.amazonaws.com"]
    }
  }

  # Aliases are a separate resource from the key they point at, and unlike
  # keys they DO have names — so they get a real ARN fence. The pattern is
  # `insolvia*`, not `insolvia-*`, so that the equally idiomatic
  # `alias/insolvia/case-staging` form is not silently denied.
  #
  # `key/*` is the second half of the same call: CreateAlias authorizes
  # against BOTH the alias and its target key, and keys have no name to fence.
  # The widening is small — this grants naming rights over a key, not any
  # access to it, and the name itself still has to match insolvia*.
  statement {
    sid = "EncryptionKeyAliases"
    actions = [
      "kms:CreateAlias",
      "kms:DeleteAlias",
      "kms:UpdateAlias",
    ]
    resources = [
      "arn:aws:kms:*:${data.aws_caller_identity.current.account_id}:alias/insolvia*",
      "arn:aws:kms:*:${data.aws_caller_identity.current.account_id}:key/*",
    ]
  }

  # Issue 8.2 names "audit logging of data access" in scope, so a trail is a
  # stated requirement rather than speculation. Worth knowing what it can and
  # cannot prove before 8.2 designs against it: because ADR 0001 makes the
  # API's execution role the ONLY application principal, CloudTrail data
  # events on the case store would show `insolvia-api-<env>-role` for every
  # read and never the end user behind it. It is therefore evidence about
  # administrative and control-plane access — the "no human read paths in
  # prod" claim — while "which signed-in user read this SSN" has to be an
  # application-level log the API writes itself, which needs no grant here.
  statement {
    sid = "AuditTrails"
    actions = [
      "cloudtrail:CreateTrail",
      "cloudtrail:DeleteTrail",
      "cloudtrail:UpdateTrail",
      "cloudtrail:StartLogging",
      "cloudtrail:StopLogging",
      "cloudtrail:GetTrail",
      "cloudtrail:GetTrailStatus",
      "cloudtrail:GetEventSelectors",
      "cloudtrail:PutEventSelectors",
      # The provider's trail read calls GetInsightSelectors unconditionally and
      # only tolerates InsightNotEnabled / UnsupportedOperation — an
      # AccessDenied here fails the refresh even with insights switched off.
      "cloudtrail:GetInsightSelectors",
      "cloudtrail:PutInsightSelectors",
      "cloudtrail:AddTags",
      "cloudtrail:RemoveTags",
      "cloudtrail:ListTags",
    ]
    resources = ["arn:aws:cloudtrail:*:${data.aws_caller_identity.current.account_id}:trail/insolvia-*"]
  }

  # cloudtrail:DescribeTrails and ListTrails do not support resource-level
  # permissions — same class as the EnumerationApis statement below, and
  # Terraform calls them on every refresh of a trail resource.
  statement {
    sid = "AuditTrailEnumeration"
    actions = [
      "cloudtrail:DescribeTrails",
      "cloudtrail:ListTrails",
    ]
    resources = ["*"]
  }

  # Case documents — credit reports, pay stubs, bank statements. A separate
  # bucket family from insolvia-web-* / insolvia-mailer-*, and the prefix is
  # left broad (insolvia-case-*) so that issues 8.2 and 8.6 can settle exact
  # bucket names without another human-applied round trip through this file.
  statement {
    sid     = "CaseDocumentBuckets"
    actions = ["s3:*"]
    resources = [
      "arn:aws:s3:::insolvia-case-*",
      "arn:aws:s3:::insolvia-case-*/*",
    ]
  }

  # The trail's own destination bucket, in its own family so the grant that
  # manages case documents is not the same grant that manages the log of
  # access to them. Object deletion is denied separately below — a bucket
  # family alone is a naming convention, not a control, since one role holds
  # both.
  statement {
    sid     = "AuditLogBuckets"
    actions = ["s3:*"]
    resources = [
      "arn:aws:s3:::insolvia-audit-*",
      "arn:aws:s3:::insolvia-audit-*/*",
    ]
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
      "kms:ListKeys",
      "kms:ListAliases",
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

  # The control that makes EncryptionKeyManagement's "*" safe, and the reason
  # the key statements above do not bother with a tag fence.
  #
  # The pipeline owns the case key's POLICY, and a KMS key policy authorizes a
  # principal on its own — so kms:PutKeyPolicy is, by itself, a path to
  # reading every SSN in the store. An identity-policy deny is the only thing
  # that overrides a key-policy allow, which is what this is.
  #
  # The ViaService carve-out keeps ParameterEncryption working: SSM
  # SecureStrings legitimately need Decrypt/Encrypt/GenerateDataKey, but only
  # ever with SSM as the calling service. A direct Decrypt carries no
  # kms:ViaService key at all, so StringNotEquals matches and the deny bites.
  # Case data is unaffected in normal operation: DynamoDB and S3 decrypt under
  # a grant as their own service principal, never as this role.
  statement {
    sid    = "DenyCaseDataDecryption"
    effect = "Deny"
    actions = [
      "kms:Decrypt",
      "kms:GenerateDataKey",
      "kms:GenerateDataKeyWithoutPlaintext",
      "kms:GenerateDataKeyPair",
      "kms:GenerateDataKeyPairWithoutPlaintext",
      "kms:ReEncryptFrom",
      "kms:ReEncryptTo",
    ]
    resources = ["*"]

    condition {
      test     = "StringNotEquals"
      variable = "kms:ViaService"
      values = [
        "ssm.${var.aws_region}.amazonaws.com",
        # CloudTrail asks the CALLER for GenerateDataKey when a trail sets
        # kms_key_id. Carved out here and allowed in TrailLogEncryption above
        # — the two go together, and neither works alone.
        "cloudtrail.${var.aws_region}.amazonaws.com",
        # Lambda does the same for function code and environment variables,
        # against the AWS-managed `alias/aws/lambda` key. Paired with
        # LambdaCodeAndEnvEncryption above, same rule: both or neither.
        #
        # Scope is worth stating plainly, since this deny is what makes
        # kms:PutKeyPolicy safe. Adding a service to this list does NOT open
        # case data — it opens what THAT service decrypts for this role.
        # Case data is reached through dynamodb/s3 as their own principals,
        # which carry a different kms:ViaService and stay denied.
        "lambda.${var.aws_region}.amazonaws.com",
      ]
    }
  }

  # An audit log the pipeline can erase is not an audit log. Terraform may
  # create, configure and tag the trail's bucket, but never remove what has
  # been written to it — including versioned overwrites, which are the obvious
  # way around a plain DeleteObject deny.
  #
  # This is a floor, not tamper-proofing, and the gaps are worth naming so the
  # next reader does not over-trust it. AuditLogBuckets still carries s3:*, so
  # the same role can expire the logs with a one-day lifecycle rule, suspend
  # versioning, or delete the bucket outright — none of which route through
  # DeleteObject. Denying those too would take away the retention
  # configuration the trail legitimately needs. Real immutability is S3 Object
  # Lock, and that is a decision for 8.2 rather than something to fake here.
  statement {
    sid    = "DenyAuditLogErasure"
    effect = "Deny"
    actions = [
      "s3:DeleteObject",
      "s3:DeleteObjectVersion",
    ]
    resources = ["arn:aws:s3:::insolvia-audit-*/*"]
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

