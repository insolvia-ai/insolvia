#!/usr/bin/env bash
#
# Derive a backend Lambda's environment from SSM — the ONE derivation every
# deploy workflow uses since the switch (ADR 0028, plan 18.22), so the five
# .NET hosts and the migrator cannot each grow a slightly different jq.
#
#   lambda-environment.sh <env> <namespace> [--db] [--db-master] [--expect-env <value>]
#
#   <env>         staging | prod                  (the SSM path's env segment)
#   <namespace>   api | worker | admin-api | mcp | migrator
#   --db          also read /insolvia/<env>/db/{endpoint,port,database-name}
#                 as INSOLVIA_DB_ENDPOINT / _PORT / _DATABASE_NAME — the
#                 database contract modules/aurora's parameters publish, in
#                 the names backend/src/Insolvia.Data/Connections/
#                 DatabaseSettings.cs reads (this is where the infra's
#                 /db/<kebab> naming and the backend's INSOLVIA_DB_* meet)
#   --db-master   also INSOLVIA_DB_MASTER_SECRET_ARN (the migrator only — an
#                 ARN, not the secret, but no service needs to know it)
#   --expect-env  refuse unless INSOLVIA_ENV equals this (staging|production):
#                 an empty or half-applied namespace would otherwise wipe
#                 every variable the service reads
#
# Prints {"Variables": {...}} — the shape `aws lambda
# update-function-configuration --environment` takes. The namespace's own
# parameters map by their last path segment, upper-cased, hyphens to
# underscores (/insolvia/staging/api/auth-client-id -> AUTH_CLIENT_ID), the
# convention every module's SSM comment describes.
#
# What it refuses, by name:
#   - a variable Lambda reserves (AWS_REGION and friends): Lambda sets them
#     itself, and an update naming one fails with "Reserved keys used in this
#     request". The hosts read AWS_REGION from the runtime.
#   - a /db/* parameter that is missing when --db asks for it.
#
# SSM_GET_PARAMETERS_BY_PATH overrides the lookup (the unit test's seam): a
# command that takes a path and prints `aws ssm get-parameters-by-path
# --query 'Parameters[].{Name:Name,Value:Value}' --output json`'s output.
#
# Bash 3.2 compatible; needs jq.
set -euo pipefail

usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; /^set -euo/d' >&2; exit 2; }

[ $# -ge 2 ] || usage
ENV_NAME="$1"
NAMESPACE="$2"
shift 2
DB=false
DB_MASTER=false
EXPECT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --db) DB=true ;;
    --db-master) DB=true; DB_MASTER=true ;;
    --expect-env) EXPECT="${2:?--expect-env needs a value}"; shift ;;
    *) usage ;;
  esac
  shift
done

lookup() {
  if [ -n "${SSM_GET_PARAMETERS_BY_PATH:-}" ]; then
    "$SSM_GET_PARAMETERS_BY_PATH" "$1"
  else
    aws ssm get-parameters-by-path \
      --path "$1" \
      --with-decryption \
      --query 'Parameters[].{Name:Name,Value:Value}' \
      --output json
  fi
}

service="$(lookup "/insolvia/$ENV_NAME/$NAMESPACE" |
  jq -c 'map({key: (.Name | split("/") | last | ascii_upcase | gsub("-"; "_")), value: .Value}) | from_entries')"

database='{}'
if $DB; then
  wanted='["endpoint", "port", "database-name"]'
  if $DB_MASTER; then wanted='["endpoint", "port", "database-name", "master-secret-arn"]'; fi
  database="$(lookup "/insolvia/$ENV_NAME/db" |
    jq -c --argjson wanted "$wanted" '
      map({key: (.Name | split("/") | last), value: .Value})
      | map(select(.key as $k | $wanted | index($k)))
      | map({key: ("INSOLVIA_DB_" + (.key | ascii_upcase | gsub("-"; "_"))), value: .value})
      | from_entries')"
  missing="$(jq -rn --argjson got "$database" --argjson wanted "$wanted" '
    $wanted | map("INSOLVIA_DB_" + (ascii_upcase | gsub("-"; "_"))) | map(select(. as $k | $got | has($k) | not)) | join(" ")')"
  if [ -n "$missing" ]; then
    echo "::error::/insolvia/$ENV_NAME/db is incomplete — missing $missing. Has the env's infra (module.aurora's parameters) applied?" >&2
    exit 1
  fi
fi

environment="$(jq -cn --argjson s "$service" --argjson d "$database" '{Variables: ($s + $d)}')"

reserved="$(echo "$environment" | jq -r '.Variables | keys[] | select(test("^(AWS_|_HANDLER$|_X_AMZN_|LAMBDA_)"))' | tr '\n' ' ')"
if [ -n "${reserved// /}" ]; then
  echo "::error::/insolvia/$ENV_NAME/$NAMESPACE publishes Lambda-reserved variable(s): $reserved— Lambda sets these itself; remove the parameter." >&2
  exit 1
fi

if [ -n "$EXPECT" ]; then
  if ! echo "$environment" | jq -e --arg want "$EXPECT" '.Variables.INSOLVIA_ENV == $want' >/dev/null; then
    echo "::error::/insolvia/$ENV_NAME/$NAMESPACE has no INSOLVIA_ENV=$EXPECT — refusing to replace the Lambda's environment with it." >&2
    exit 1
  fi
fi

echo "$environment"
