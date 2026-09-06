FROM lukemathwalker/cargo-chef:0.1.78-rust-1.98.0-trixie AS chef
WORKDIR /workspace
RUN apt-get update \
    && apt-get install --yes --no-install-recommends musl-tools \
    && rm -rf /var/lib/apt/lists/* \
    && rustup target add x86_64-unknown-linux-musl

FROM chef AS planner
COPY . .
RUN cargo chef prepare --recipe-path recipe.json

FROM chef AS builder
COPY --from=planner /workspace/recipe.json recipe.json
RUN cargo chef cook --release --locked --target x86_64-unknown-linux-musl --recipe-path recipe.json
COPY . .
RUN cargo build --release --locked --target x86_64-unknown-linux-musl

FROM chef AS development
COPY . .
RUN cargo build --locked
ENTRYPOINT ["/workspace/target/debug/media-guard"]

FROM gcr.io/distroless/static-debian13:nonroot AS final
WORKDIR /app
COPY --from=builder --chown=nonroot:nonroot /workspace/target/x86_64-unknown-linux-musl/release/media-guard /app/media-guard
USER nonroot:nonroot
EXPOSE 8092
ENTRYPOINT ["/app/media-guard"]

