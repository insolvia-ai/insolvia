# The auth contract, READ: the Cognito pool's public identifiers, from the SSM
# parameters insolvia-services publishes (its modules/auth/contract.tf) —
# /<project>/<environment>/auth/*. Auth is that repo's since ADR 0026; this
# module is how every root here learns about it. Nothing else here reads the
# pool, its state, or its names.
#
# It is the pattern humbugg uses for its shared dev pool. The reason to prefer
# it over looking the pool up by name: a missing or renamed parameter fails
# the plan with the parameter's own name in the error — the most specific
# failure on offer — while a stale client id in the API's configuration would
# fail every sign-in quietly.
#
# ORDER. The parameters must exist before a root that reads them can plan:
#   • staging, prod — insolvia-services' env root applies first (its release);
#   • dev — insolvia-services' shared dev pool (scripts/apply-dev-auth.sh
#     there, run once); every developer machine's infra/envs/dev reads it.
# A NEW key this repo must read is added there, released there, before the PR
# here that reads it — the same rule as a new service's container repository.
#
# Values are public identifiers (they ride in every sign-in redirect), which
# is why they are plain Strings there and `insecure_value` here.

locals {
  prefix = "/${var.project}/${var.environment}/auth"
  keys   = toset(["user-pool-id", "user-pool-arn", "client-id", "portal-client-id", "domain", "issuer-url", "mcp-client-ids"])
}

data "aws_ssm_parameter" "contract" {
  for_each = local.keys
  name     = "${local.prefix}/${each.key}"
}

locals {
  value = { for key, parameter in data.aws_ssm_parameter.contract : key => parameter.insecure_value }

  # {harness: client id}. Every MCP harness client registered on the pool is in
  # it, so registering a harness in insolvia-services IS adding it to the MCP
  # service's allowlist, with nothing hand-listed on either side.
  mcp_clients = jsondecode(local.value["mcp-client-ids"])
}
