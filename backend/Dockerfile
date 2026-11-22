# Alondra1 API image. Build from the repository root:
#   docker build --build-arg VERSION=1.0.0 -t alondra1-api:1.0.0 backend
# The release workflow builds, scans, signs, and publishes it; deploy by digest (docs/runbooks/release.md).
FROM golang:1.27-alpine AS build
ARG VERSION=dev
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w -X main.version=${VERSION}" -o /out/api ./cmd/api

FROM gcr.io/distroless/static-debian12:nonroot
ARG VERSION=dev
LABEL org.opencontainers.image.title="alondra1-api" org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.source="https://github.com/nikita4sea/alondra1"
COPY --from=build /out/api /api
USER nonroot:nonroot
EXPOSE 8080 9090
HEALTHCHECK --interval=15s --timeout=5s --retries=3 CMD ["/api", "healthcheck"]
ENTRYPOINT ["/api"]
