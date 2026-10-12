# The contract: the pool's public identifiers, published to SSM.
#
# PREPARATION for auth moving to insolvia-services (ADR 0026, its PR). After
# the move this repo reads the pool through these parameters instead of this
# module's outputs, and insolvia-services' modules/auth publishes them — from
# a contract.tf identical to this one, at the same addresses
# (module.auth.aws_ssm_parameter.contract["<key>"]). They are created HERE
# first, by this repo's current module, for one reason: the move's
# verification plan of this repo has to be able to read them, and they then
# travel with the rest of the module in the state move, unchanged.
#
# Keep the code below equivalent to insolvia-services'
# infra/modules/auth/contract.tf until the move has run; any difference is a
# planned change on the far side of the move, which the move refuses.
#
#   /<project>/<env>/auth/user-pool-id      the pool's id (API calls)
#   /<project>/<env>/auth/user-pool-arn     the pool's ARN (IAM grants)
#   /<project>/<env>/auth/client-id         insolvia-<env>-app, the web SPA
#   /<project>/<env>/auth/portal-client-id  insolvia-<env>-portal
#   /<project>/<env>/auth/domain            sign-in hostname (custom if set)
#   /<project>/<env>/auth/issuer-url        what tokens are verified against
#   /<project>/<env>/auth/mcp-client-ids    JSON {harness: client id} — the
#                                           MCP service's allowlist
#
# Every environment publishes them, dev-<machine> included — harmless there,
# and the per-machine pool they describe is replaced by a shared one once the
# move lands.

locals {
  domain = (
    var.custom_domain == null
    ? "${aws_cognito_user_pool_domain.main.domain}.auth.${data.aws_region.current.region}.amazoncognito.com"
    : var.custom_domain
  )

  contract = {
    "user-pool-id"     = aws_cognito_user_pool.main.id
    "user-pool-arn"    = aws_cognito_user_pool.main.arn
    "client-id"        = aws_cognito_user_pool_client.web.id
    "portal-client-id" = aws_cognito_user_pool_client.portal.id
    "domain"           = local.domain
    "issuer-url"       = "https://cognito-idp.${data.aws_region.current.region}.amazonaws.com/${aws_cognito_user_pool.main.id}"
    "mcp-client-ids"   = jsonencode({ for harness, client in aws_cognito_user_pool_client.mcp : harness => client.id })
  }
}

resource "aws_ssm_parameter" "contract" {
  for_each = local.contract

  name  = "/${var.project}/${var.environment}/auth/${each.key}"
  type  = "String"
  value = each.value
  tags  = var.tags
}
