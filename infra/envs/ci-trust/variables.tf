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
