FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:afd0ccff9d306812a69ef0f47458376b6acb544ae866cfc4b8d28ff0eef35ea7 AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374

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
