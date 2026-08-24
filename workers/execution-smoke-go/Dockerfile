FROM golang:1.27.0@sha256:65b6f280bf050ec5af12716857e8ea8439d694dbba8f31ceeb7630670071f2bb AS build

WORKDIR /src
COPY clients/go/go.mod ./clients/go/go.mod
COPY workers/execution-smoke-go/go.mod ./workers/execution-smoke-go/go.mod
COPY clients/go ./clients/go
COPY workers/execution-smoke-go ./workers/execution-smoke-go
WORKDIR /src/workers/execution-smoke-go
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags='-s -w' -o /out/execution-smoke-worker ./

FROM gcr.io/distroless/static-debian12:nonroot@sha256:1b7b9f0f0e0a1d2155f531db587cc48ec26aaf97ab64364225f5bf18a054e66a
ARG VYRAL_IMAGE_VERSION=0.3.0
ARG VYRAL_IMAGE_REVISION=local
LABEL org.opencontainers.image.title="Vyral Execution Smoke Worker" \
      org.opencontainers.image.description="Qualification fixture for Vyral's public external-worker protocol" \
      org.opencontainers.image.source="https://github.com/univeracity/vyral" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="$VYRAL_IMAGE_VERSION" \
      org.opencontainers.image.revision="$VYRAL_IMAGE_REVISION"
COPY --from=build /out/execution-smoke-worker /execution-smoke-worker
USER nonroot:nonroot
EXPOSE 8080
ENTRYPOINT ["/execution-smoke-worker"]
