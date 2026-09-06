# ADR 0018: Isolate hostile image decoding in Rust

Status: Accepted. Issue: #73. Date: 2026-09-05.

## Context and decision

CNH uploads arrive by HTTP and RabbitMQ. Both now call the same Rust pixel
sanitizer before MinIO sees any bytes. PNG, BMP and JPEG are detected by content,
decoded with a 4096-pixel side limit and re-encoded as RGB PNG. The 8 MiB input
limit, two CPU workers and 768 MiB container limit bound hostile work. A fresh
256-pixel thumbnail contains no original metadata either.

Rust fits binary parsing and CPU work without a tracing collector or a native
unsafe image binding. The image crate still needs security updates; memory
safety is not a substitute for resource limits. Its decoder allocation bound
is best effort, so strict dimensions and a container limit remain necessary.
See the [image limits contract](https://docs.rs/image/0.25.10/image/struct.Limits.html).

## Alternatives and cost

A gateway library avoids an HTTP hop but does not protect the existing RabbitMQ
consumer. A .NET decoder avoids another runtime, but Rust isolates hostile work
from the rider process and makes CPU concurrency explicit. A separate service
costs one hop and deployment; no storage credentials are given to it. It is
internal only, with no published port. The existing signed gateway and queue
identity boundaries remain the upload entry points.

## Storage and failure behavior

RiderManager stores only object keys. Read responses mint five-minute URLs;
existing persisted URLs are cleared by a data migration. Keys use a hash of the
authenticated rider id and a random upload id (stable hashed command identity
for queue replay). The original and thumbnail are removed before rider deletion;
storage failure leaves the rider and pointer available for retry. Existing
presigned links remain capabilities until expiry, but deleted objects cannot be
read through them. Replacements remove the old object after saving the new key.

Sanitizer failure refuses uploads (503 and Retry-After); it never falls back to
raw content. Registration without an image, listings and rentals continue.
The process has live, startup and ready probes, OTLP spans/logs with incoming
trace context, and graceful shutdown. Decoder errors are 422. Queue failures
follow the existing durable bounded retry and DLQ contract.
