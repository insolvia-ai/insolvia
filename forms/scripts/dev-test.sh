#!/usr/bin/env bash
# Validate the form field specs exactly as CI does: the spec tests in
# backend/tests/Insolvia.Core.Tests/Forms/Specs (xUnit, ADR 0029), filtered out of
# that project so a spec edit runs in seconds and needs only the .NET SDK
# backend/global.json pins. Extra arguments go to `dotnet test` (CI passes
# -p:RestoreLockedMode=true -c Release).
set -euo pipefail

cd "$(dirname "$0")/../../backend"
exec dotnet test tests/Insolvia.Core.Tests \
  --filter "FullyQualifiedName~Insolvia.Forms.Tests.Specs" \
  --logger "console;verbosity=normal" \
  "$@"
