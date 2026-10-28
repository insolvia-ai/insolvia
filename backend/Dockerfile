# The backend's images. Build context is backend/ (this directory):
#
#   docker build --target lambda                -t insolvia-api .        # what a deploy ships
#   docker build --target development           -t insolvia-api .        # what compose runs
#   docker build --target migrator              -t insolvia-migrator .   # the migrator Lambda
#   docker build --target migrator-development  -t insolvia-migrator .   # compose's one-shot
#
# One build stage compiles every host; each deployable unit gets its own final
# stage(s), so a host's weight lands in its own image and never in another's
# (ADR 0015). The API and the migrator have stages today. Worker, Admin, Mcp
# and Filing each get a `<host>` stage on the Lambda base image (and Admin a
# `<host>-development` stage, port 8090) in the PR that gives them a handler:
# publish that project into /out/<host> in the build stage and copy it the
# same way the lambda stage below does.

# ── build ───────────────────────────────────────────────────────────────────
# The SDK is the exact one global.json pins. It runs on the BUILD platform:
# the output is portable IL, so cross-building for arm64 or amd64 needs no
# emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src

# Restore first, from the project graph alone, so a source-only change reuses
# the restore layer. --locked-mode refuses a restore that would change any
# packages.lock.json: the image gets exactly the reviewed dependency graph.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig Insolvia.slnx ./
COPY src/Insolvia.Core/*.csproj src/Insolvia.Core/packages.lock.json src/Insolvia.Core/
COPY src/Insolvia.Engines/*.csproj src/Insolvia.Engines/packages.lock.json src/Insolvia.Engines/
COPY src/Insolvia.Forms/*.csproj src/Insolvia.Forms/packages.lock.json src/Insolvia.Forms/
COPY src/Insolvia.Data/*.csproj src/Insolvia.Data/packages.lock.json src/Insolvia.Data/
COPY src/Insolvia.Aws/*.csproj src/Insolvia.Aws/packages.lock.json src/Insolvia.Aws/
COPY src/Insolvia.Api/*.csproj src/Insolvia.Api/packages.lock.json src/Insolvia.Api/
COPY src/Insolvia.Migrator/*.csproj src/Insolvia.Migrator/packages.lock.json src/Insolvia.Migrator/
RUN dotnet restore src/Insolvia.Api/Insolvia.Api.csproj --locked-mode \
 && dotnet restore src/Insolvia.Migrator/Insolvia.Migrator.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/Insolvia.Api/Insolvia.Api.csproj \
      --configuration Release --no-restore --output /out/api \
 && dotnet publish src/Insolvia.Migrator/Insolvia.Migrator.csproj \
      --configuration Release --no-restore --output /out/migrator

# ── lambda: the API as AWS runs it ──────────────────────────────────────────
# The AWS base image carries the .NET and ASP.NET Core runtimes and the
# Runtime Interface Emulator. The CMD is an executable assembly's name, so the
# image's bootstrap runs `dotnet Insolvia.Api.dll`, and AddAWSLambdaHosting
# (Program.cs) switches Kestrel for the Lambda runtime client.
FROM public.ecr.aws/lambda/dotnet:10 AS lambda
COPY --from=build /out/api ${LAMBDA_TASK_ROOT}/
CMD ["Insolvia.Api"]

# ── development: the same build under Kestrel, for docker compose ───────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS development
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    INSOLVIA_ENV=local
COPY --from=build /out/api ./
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Insolvia.Api.dll"]

# ── migrator: the one-shot Lambda that migrates and provisions roles ────────
# Invoked by CI before the `live` alias shifts (ADR 0028 § Runtime shape).
# Program.cs runs one migration per invocation under the Lambda runtime
# client, and exits after one run anywhere else.
FROM public.ecr.aws/lambda/dotnet:10 AS migrator
COPY --from=build /out/migrator ${LAMBDA_TASK_ROOT}/
CMD ["Insolvia.Migrator"]

# ── migrator-development: the same build as compose's one-shot ──────────────
# No ASP.NET Core: the migrator is a console program on the plain runtime.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrator-development
WORKDIR /app
COPY --from=build /out/migrator ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Insolvia.Migrator.dll"]
