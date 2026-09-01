FROM lukemathwalker/cargo-chef:0.1.78-rust-1.98.0-trixie AS chef
WORKDIR /workspace

FROM chef AS planner
COPY . .
RUN cargo chef prepare --recipe-path recipe.json

FROM chef AS builder
COPY --from=planner /workspace/recipe.json recipe.json
RUN cargo chef cook --release --locked --recipe-path recipe.json
COPY . .
RUN cargo build --release --locked

FROM chef AS development
COPY . .
RUN cargo build --locked
ENTRYPOINT ["/workspace/target/debug/api-gateway"]

FROM gcr.io/distroless/cc-debian13:nonroot AS final
WORKDIR /app
COPY --from=builder --chown=nonroot:nonroot /workspace/target/release/api-gateway /app/api-gateway
USER nonroot:nonroot
EXPOSE 8090
ENTRYPOINT ["/app/api-gateway"]
