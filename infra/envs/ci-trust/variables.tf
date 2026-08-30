variable "aws_region" {
  description = "Default AWS region."
  type        = string
  default     = "us-east-1"
}

variable "github_repo" {
  description = <<-EOT
    owner/name of the repo allowed to assume the deploy role. Must match the
    canonical case GitHub emits in the OIDC `sub` claim (the IAM condition is
    case-sensitive) — i.e. the org's exact login casing.
  EOT
  type        = string
  default     = "insolvia-ai/insolvia"
}

variable "github_immutable_sub_prefix" {
  description = <<-EOT
    The org enforces GitHub's IMMUTABLE OIDC subject claims, so the `sub` is
    built from numeric ids — `repo:<org>@<org_id>/<repo>@<repo_id>` — not the
    mutable `owner/name`. This is the immutable prefix (everything before the
    trailing `:<job-context>`), read from
    `gh api repos/<owner>/<repo>/actions/oidc/customization/sub .sub_claim_prefix`.

    It is pinned to the repo's numeric id ON PURPOSE (that is the security value
    of immutable subjects): if the repository is ever deleted and recreated it
    gets a NEW id and this must be updated, or `AssumeRoleWithWebIdentity` fails
    with "Not authorized". org_id=305033818, repo_id=1312821833.
  EOT
  type        = string
  default     = "repo:insolvia-ai@305033818/insolvia@1312821833"
}

variable "staging_user_pool_arn" {
  description = <<-EOT
    ARN of the STAGING Cognito user pool, so the seed role can provision the
    e2e test accounts in it — and in nothing else.

    OPTIONAL, AND THAT IS A BOOTSTRAP CONCESSION rather than laziness. A pool's
    ARN contains a generated id, and this root is applied by a human BEFORE
    `staging` exists (docs/runbooks/aws-bootstrap.md's apply order). A
    `terraform_remote_state` lookup would therefore fail on a fresh account, so
    the grant is conditional instead: leave this empty on first bootstrap and
    the statement is simply absent, then set it once staging has applied.

    Get it with:
      terraform -chdir=infra/envs/staging output -raw auth_user_pool_arn

    THE SAME CONCESSION BITES ON A POOL REPLACEMENT, not just on bootstrap.
    Anything that recreates the staging pool — the naming rename did, since a
    pool name change is a destroy-and-recreate — mints a NEW id, so the value
    here goes stale and this root needs a second human apply with the new ARN.
    The symptom is not a failed apply: it is the seed step in app-staging.yml
    failing with AccessDenied against a pool that no longer exists.

    WHY NOT A WILDCARD. `userpool/*` would reach PROD's pool, and the actions
    below include AdminSetUserPassword — which on a prod pool is the ability to
    take over any customer account. There is no scoping condition that fixes
    that; only the exact ARN does, which is why this variable exists at all.
  EOT
  type        = string
  default     = ""
}
