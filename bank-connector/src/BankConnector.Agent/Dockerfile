# Bank Connector agent - the data plane.
#
# Build context is the REPO ROOT:
#   docker build -f bank-connector/src/BankConnector.Agent/Dockerfile .
#
# This is the ONLY image with browser binaries and the only one that ever
# holds a plaintext credential. Two properties follow, and both are
# enforced here rather than by convention:
#
#   * It gets NO database credential. An agent leases work over HTTP and
#     has no route to Postgres at all (see deploy/docker-compose.yml, where
#     it is not even on the data network).
#   * It publishes NO port. Every call is outbound from the agent, which is
#     what lets one run behind NAT on a residential line or on a user's own
#     machine without an inbound hole.
#
# amd64 only in CI: Chromium under Playwright on arm64 is unverified and is
# a named open question in the platform design (s12.2). Build it here if
# you want to try - nothing in this file is arch-specific except the RID
# below, which exists for the reason in the publish comment.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from project files alone, so editing a .cs file does not
# re-resolve the package graph. Any new project in this image's reference
# chain has to be added here as well.
COPY Directory.Build.props Directory.Packages.props ./
COPY connector-kit/src/Connector.Kit/Connector.Kit.csproj connector-kit/src/Connector.Kit/
COPY connector-kit/src/Connector.Kit.Agent/Connector.Kit.Agent.csproj connector-kit/src/Connector.Kit.Agent/
COPY bank-connector/src/BankConnector.Adapters/BankConnector.Adapters.csproj bank-connector/src/BankConnector.Adapters/
COPY bank-connector/src/BankConnector.Agent/BankConnector.Agent.csproj bank-connector/src/BankConnector.Agent/
RUN dotnet restore bank-connector/src/BankConnector.Agent/BankConnector.Agent.csproj -r linux-x64

COPY connector-kit/src/ connector-kit/src/
COPY bank-connector/src/ bank-connector/src/

# Self-contained on purpose. The Playwright image is here for BROWSERS;
# tying the agent to whatever .NET version that image happens to ship would
# couple an agent release to Playwright's base-image cadence, and a
# mismatch is a runtime failure on a residential box nobody is watching.
RUN dotnet publish bank-connector/src/BankConnector.Agent/BankConnector.Agent.csproj \
    -c Release -r linux-x64 --self-contained true -o /app --no-restore \
    -p:ContinuousIntegrationBuild=true

# Tag tracks the Microsoft.Playwright version in Directory.Packages.props.
# The driver and the browser bundles are versioned together; a drift
# between them fails at launch, not at build.
FROM mcr.microsoft.com/playwright/dotnet:v1.61.0-noble

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    Agent__ProfileRoot=/profiles \
    Agent__WorkRoot=/work

ARG BUILD_NUMBER=0
ENV BUILD_NUMBER=${BUILD_NUMBER}

LABEL org.opencontainers.image.title="bank-connector-agent" \
      org.opencontainers.image.description="Bank Connector agent - leases jobs, drives adapters, relays challenges" \
      org.opencontainers.image.source="https://github.com/okkes/munniscrape"

# /profiles is the T4 persistent-profile store and the one directory that
# must survive a restart - it holds a logged-in browser session on hardware
# the user controls. /work is scratch for a single job and is emptied when
# it ends, including on failure: a downloaded bank statement left on disk
# is exactly the leak this architecture exists to prevent.
RUN mkdir -p /profiles /work /app && chown -R pwuser /profiles /work /app

WORKDIR /app
COPY --from=build --chown=pwuser /app .

# pwuser ships with the Playwright image and owns the browser bundles. A
# browser rendering a hostile page must never be root.
USER pwuser
ENTRYPOINT ["./BankConnector.Agent"]
