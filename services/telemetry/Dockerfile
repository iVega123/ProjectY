FROM elixir:1.18.4-otp-27-alpine AS development
WORKDIR /workspace
RUN apk upgrade --no-cache && apk add --no-cache build-base cmake protobuf curl
RUN mix local.hex --force && mix local.rebar --force
COPY mix.exs mix.lock ./
RUN mix deps.get
COPY . .
RUN mix compile
ENTRYPOINT ["mix", "run", "--no-halt"]

FROM development AS builder
ENV MIX_ENV=prod
RUN mix deps.get --only prod && mix compile && mix release

FROM alpine:3.24 AS final
ENV LANG=C.UTF-8
RUN apk upgrade --no-cache && apk add --no-cache libstdc++ openssl ncurses-libs ca-certificates curl
WORKDIR /app
COPY --from=builder /workspace/_build/prod/rel/projecty_telemetry ./
USER 65532:65532
EXPOSE 4000
ENTRYPOINT ["/app/bin/projecty_telemetry", "start"]
