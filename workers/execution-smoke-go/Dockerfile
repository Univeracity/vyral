FROM golang:1.26.6@sha256:640a234f4bea3e399c056b7b8f9c667c4939befae8db2f14e9785e16eccd4205 AS build

WORKDIR /src
COPY clients/go/go.mod ./clients/go/go.mod
COPY workers/execution-smoke-go/go.mod ./workers/execution-smoke-go/go.mod
COPY clients/go ./clients/go
COPY workers/execution-smoke-go ./workers/execution-smoke-go
WORKDIR /src/workers/execution-smoke-go
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags='-s -w' -o /out/execution-smoke-worker ./

FROM gcr.io/distroless/static-debian12:nonroot@sha256:f5b485ea962d9bd1186b2f6b3a061191539b905b82ec395de78cbfae51f20e35
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
