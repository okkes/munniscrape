# The demo consumer - munni's relay, as a runnable reference implementation.
#
# Build context is the REPO ROOT, like every other image here, so that the
# central package versions in Directory.Packages.props are inside it:
#
#   docker build -f demo-client/src/DemoClient.Web/Dockerfile .
#
# This image references NO connector project and must not start doing so.
# The relay talks HTTP and nothing else, exactly as Munni.Api will, and a
# ProjectReference would let a wire-contract mistake compile cleanly
# against types this side should never have seen.
#
# There are no browser binaries here and no database credential. The demo
# holds sealed bundles it cannot open, and the browser it serves holds them
# in sessionStorage, where they die with the tab.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore from the project file alone, so editing a .cs or a .js file does
# not re-resolve the package graph.
COPY Directory.Build.props Directory.Packages.props ./
COPY demo-client/src/DemoClient.Web/DemoClient.Web.csproj demo-client/src/DemoClient.Web/
RUN dotnet restore demo-client/src/DemoClient.Web/DemoClient.Web.csproj

COPY demo-client/src/ demo-client/src/
RUN dotnet publish demo-client/src/DemoClient.Web/DemoClient.Web.csproj \
    -c Release -o /app --no-restore -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# The relay formats money and dates for display, and invariant mode changes
# both. tzdata for the same reason it is in the connector images: a receipt
# near midnight belongs to the day Europe/Amsterdam says it does.
RUN apk add --no-cache icu-libs tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ARG BUILD_NUMBER=0
ENV BUILD_NUMBER=${BUILD_NUMBER}

LABEL org.opencontainers.image.title="connector-demo-client" \
      org.opencontainers.image.description="Reference consumer - relays to both connectors, holds no connector credential" \
      org.opencontainers.image.source="https://github.com/okkes/munniscrape"

WORKDIR /app
COPY --from=build /app .

# appsettings.Development.json is CopyToPublishDirectory="Never", so this
# image ships base configuration only and BaseUrl/SubjectSalt arrive from
# the environment. That is deliberate: a development configuration inside a
# deployed container is how a relay ends up pointing at localhost and
# looking perfectly healthy doing it.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "DemoClient.Web.dll"]
