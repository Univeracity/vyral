FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build

WORKDIR /src
COPY . .
RUN dotnet publish src/Vyral.Server/Vyral.Server.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    && mkdir -p /app/publish/.vyral

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:f9bd6be9b5ab75b8196bff0f0972580edaea7fa8ca04e6ef530950e33caee5b0

ARG VYRAL_IMAGE_VERSION=0.3.0
ARG VYRAL_IMAGE_REVISION=local
LABEL org.opencontainers.image.title="Vyral Server" \
      org.opencontainers.image.description="Provider-portable records, retrieval, durable execution, and MCP server" \
      org.opencontainers.image.source="https://github.com/univeracity/vyral" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="$VYRAL_IMAGE_VERSION" \
      org.opencontainers.image.revision="$VYRAL_IMAGE_REVISION"

WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    CanonicalStore__Enabled=false \
    DatabasePath=/app/.vyral/vyral.sqlite \
    ObjectsPath=/app/.vyral/objects \
    Providers__ArtifactDirectory=/app/.vyral/provider-runs
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "Vyral.Server.dll"]
