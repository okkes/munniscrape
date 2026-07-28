# Shopping Connector control plane.
#
# Build context is the REPO ROOT, not this folder: the service is a thin
# shell over connector-kit and shares the repo's central package versions,
# so both must be inside the context.
#
#   docker build -f shop-connector/src/ShopConnector.Api/Dockerfile .
#
# Arch-agnostic on purpose - CI builds it natively once per architecture
# (amd64 NAS, arm64 Pi) and merges the two into one manifest tag; no QEMU.
#
# There are deliberately NO browser binaries here. The control plane never
# touches a provider, and an image without Chromium cannot start one by
# accident on the wrong egress.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore from project files alone, so editing a .cs file does not
# re-resolve the package graph. Any new project in this image's reference
# chain has to be added here as well.
COPY Directory.Build.props Directory.Packages.props ./
COPY connector-kit/src/Connector.Kit/Connector.Kit.csproj connector-kit/src/Connector.Kit/
COPY connector-kit/src/Connector.Kit.Hosting/Connector.Kit.Hosting.csproj connector-kit/src/Connector.Kit.Hosting/
COPY shop-connector/src/ShopConnector.Adapters/ShopConnector.Adapters.csproj shop-connector/src/ShopConnector.Adapters/
COPY shop-connector/src/ShopConnector.Api/ShopConnector.Api.csproj shop-connector/src/ShopConnector.Api/
RUN dotnet restore shop-connector/src/ShopConnector.Api/ShopConnector.Api.csproj

COPY connector-kit/src/ connector-kit/src/
COPY shop-connector/src/ shop-connector/src/
RUN dotnet publish shop-connector/src/ShopConnector.Api/ShopConnector.Api.csproj \
    -c Release -o /app --no-restore -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# icu-libs: money and dates are formatted and parsed server-side, and
# invariant mode silently changes both.
# tzdata: a purchase near midnight lands on the wrong day without a real
# Europe/Amsterdam offset, and a receipt on the wrong day matches the wrong
# bank transaction.
RUN apk add --no-cache icu-libs tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ARG BUILD_NUMBER=0
# Reported as service.version in the catalogue response, so an operator can
# tell which build answered a call.
ENV BUILD_NUMBER=${BUILD_NUMBER}

LABEL org.opencontainers.image.title="shop-connector-api" \
      org.opencontainers.image.description="Shopping Connector control plane - manifest, sessions, jobs, challenges" \
      org.opencontainers.image.source="https://github.com/okkes/munniscrape"

WORKDIR /app
COPY --from=build /app .

# The 'app' user ships with the base image. Nothing here needs root, and a
# service holding an unsealed credential for the length of a job is exactly
# where that matters.
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShopConnector.Api.dll"]
