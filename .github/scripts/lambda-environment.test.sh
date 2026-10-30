#!/usr/bin/env bash
#
# Unit tests for lambda-environment.sh — no AWS. A fake lookup serves SSM
# paths from fixture files ($FIXTURES/<path with / as _>.json).
#
#   .github/scripts/lambda-environment.test.sh
#
# scripts/dev-test-unit.sh runs this beside release-diff-base.test.sh (the
# `ci-scripts` area). Bash 3.2 compatible.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUT="$HERE/lambda-environment.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
export FIXTURES="$TMP/fixtures"
mkdir -p "$FIXTURES"

cat >"$TMP/lookup" <<'EOF'
#!/usr/bin/env bash
file="$FIXTURES/$(echo "$1" | tr '/' '_').json"
if [ -f "$file" ]; then cat "$file"; else echo '[]'; fi
EOF
chmod +x "$TMP/lookup"
export SSM_GET_PARAMETERS_BY_PATH="$TMP/lookup"

fixture() { cat >"$FIXTURES/$(echo "$1" | tr '/' '_').json"; }

fixture /insolvia/staging/api <<'EOF'
[
  {"Name": "/insolvia/staging/api/insolvia-env", "Value": "staging"},
  {"Name": "/insolvia/staging/api/insolvia-db-username", "Value": "api"},
  {"Name": "/insolvia/staging/api/auth-client-id", "Value": "client-123"}
]
EOF
fixture /insolvia/staging/db <<'EOF'
[
  {"Name": "/insolvia/staging/db/endpoint", "Value": "db.example"},
  {"Name": "/insolvia/staging/db/port", "Value": "5432"},
  {"Name": "/insolvia/staging/db/database-name", "Value": "insolvia"},
  {"Name": "/insolvia/staging/db/cluster-resource-id", "Value": "cluster-X"},
  {"Name": "/insolvia/staging/db/master-secret-arn", "Value": "arn:secret"}
]
EOF
fixture /insolvia/staging/bad <<'EOF'
[
  {"Name": "/insolvia/staging/bad/insolvia-env", "Value": "staging"},
  {"Name": "/insolvia/staging/bad/aws-region", "Value": "us-east-1"}
]
EOF

failures=0
check() { # name, expected, actual
  if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected $2, got $3"; failures=$((failures + 1)); fi
}

out="$("$SUT" staging api)"
check "namespace keys map by last segment" "client-123" "$(echo "$out" | jq -r '.Variables.AUTH_CLIENT_ID')"
check "no db without --db" "false" "$(echo "$out" | jq '.Variables | has("INSOLVIA_DB_ENDPOINT")')"

out="$("$SUT" staging api --db --expect-env staging)"
check "db endpoint is INSOLVIA_DB_ENDPOINT" "db.example" "$(echo "$out" | jq -r '.Variables.INSOLVIA_DB_ENDPOINT')"
check "db name is INSOLVIA_DB_DATABASE_NAME" "insolvia" "$(echo "$out" | jq -r '.Variables.INSOLVIA_DB_DATABASE_NAME')"
check "the service's role survives" "api" "$(echo "$out" | jq -r '.Variables.INSOLVIA_DB_USERNAME')"
check "no master secret for a service" "false" "$(echo "$out" | jq '.Variables | has("INSOLVIA_DB_MASTER_SECRET_ARN")')"
check "no cluster resource id" "false" "$(echo "$out" | jq '.Variables | has("INSOLVIA_DB_CLUSTER_RESOURCE_ID")')"

out="$("$SUT" staging api --db-master)"
check "the migrator gets the master secret ARN" "arn:secret" "$(echo "$out" | jq -r '.Variables.INSOLVIA_DB_MASTER_SECRET_ARN')"

if "$SUT" staging api --expect-env production >/dev/null 2>&1; then r=passed; else r=refused; fi
check "a wrong INSOLVIA_ENV is refused" "refused" "$r"

if "$SUT" staging empty --expect-env staging >/dev/null 2>&1; then r=passed; else r=refused; fi
check "an empty namespace is refused" "refused" "$r"

if "$SUT" staging bad >/dev/null 2>&1; then r=passed; else r=refused; fi
check "a reserved AWS_ variable is refused" "refused" "$r"

if "$SUT" prod api --db >/dev/null 2>&1; then r=passed; else r=refused; fi
check "a missing db contract is refused" "refused" "$r"

[ "$failures" -eq 0 ] || { echo "$failures failure(s)"; exit 1; }
echo "all passed"
