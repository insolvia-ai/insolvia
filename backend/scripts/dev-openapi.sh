#!/usr/bin/env bash
# Rewrites docs/reference/openapi/api.json and admin.json from the documents
# the API and admin hosts generate (their OpenApiDocumentTests, with
# INSOLVIA_WRITE_OPENAPI=1). Run it after changing a route, a view record or
# a request record, and commit the result: CI fails while the committed copy
# differs from what the host serves.
set -euo pipefail
cd "$(dirname "$0")/.."

export INSOLVIA_WRITE_OPENAPI=1
dotnet test tests/Insolvia.Api.Tests --filter "FullyQualifiedName~OpenApiDocumentTests.The_committed_document_is_the_one_the_api_serves"
dotnet test tests/Insolvia.Admin.Tests --filter "FullyQualifiedName~OpenApiDocumentTests.The_committed_document_is_the_one_the_admin_service_serves"
cd .. && npx prettier --log-level warn --write docs/reference/openapi/*.json
