FROM busybox:1.37.0-musl AS probe

FROM otel/opentelemetry-collector-contrib:0.121.0

COPY --from=probe /bin/busybox /busybox
