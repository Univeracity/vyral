FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build

WORKDIR /src
COPY . .
RUN dotnet restore src/Vyral.Server/Vyral.Server.csproj --locked-mode --disable-parallel \
    && dotnet restore src/Vyral.HostedWorker/Vyral.HostedWorker.csproj --locked-mode --disable-parallel \
    && dotnet publish src/Vyral.Server/Vyral.Server.csproj \
    -c Release \
    -o /app/publish/server \
    --no-restore \
    /p:UseAppHost=false \
    && dotnet publish src/Vyral.HostedWorker/Vyral.HostedWorker.csproj \
    -c Release \
    -o /app/publish/worker \
    --no-restore \
    /p:UseAppHost=false \
    && mkdir -p /app/publish/.vyral

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd

ARG VYRAL_IMAGE_VERSION=0.3.6
ARG VYRAL_IMAGE_REVISION=local
LABEL org.opencontainers.image.title="Vyral Server" \
      org.opencontainers.image.description="Provider-portable records, retrieval, durable execution, and MCP server" \
      org.opencontainers.image.source="https://github.com/univeracity/vyral" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="$VYRAL_IMAGE_VERSION" \
      org.opencontainers.image.revision="$VYRAL_IMAGE_REVISION"

WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Server__RequireApiKey=true \
    CanonicalStore__Enabled=false \
    DatabasePath=/app/.vyral/vyral.sqlite \
    ObjectsPath=/app/.vyral/objects \
    Providers__ArtifactDirectory=/app/.vyral/provider-runs
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654
EXPOSE 8080
# The default is the public API server. Deploy the same pinned image as the
# least-privilege generic worker with: dotnet worker/Vyral.HostedWorker.dll
ENTRYPOINT ["dotnet", "server/Vyral.Server.dll"]
