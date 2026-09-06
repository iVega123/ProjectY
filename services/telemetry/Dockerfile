FROM elixir:1.18.4-otp-27 AS development
WORKDIR /workspace
RUN apt-get update && apt-get install -y --no-install-recommends cmake protobuf-compiler curl && rm -rf /var/lib/apt/lists/*
RUN mix local.hex --force && mix local.rebar --force
COPY mix.exs mix.lock ./
RUN mix deps.get
COPY . .
RUN mix compile
ENTRYPOINT ["mix", "run", "--no-halt"]

FROM development AS builder
ENV MIX_ENV=prod
RUN mix deps.get --only prod && mix compile && mix release

FROM debian:bookworm-slim AS final
ENV LANG=C.UTF-8
RUN apt-get update && apt-get install -y --no-install-recommends libstdc++6 openssl libncurses6 ca-certificates curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=builder /workspace/_build/prod/rel/projecty_telemetry ./
USER 65532:65532
EXPOSE 4000
ENTRYPOINT ["/app/bin/projecty_telemetry", "start"]
