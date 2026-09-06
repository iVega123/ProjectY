# Original epic 10 acceptance evidence

Measured locally on 2026-09-06 against the isolated `projecty-load` stack.
These probes are functional acceptance evidence, not a production capacity claim.

| Check | Observed result |
| --- | --- |
| Rust media unit/codec tests | 3 passed; clippy with warnings denied passed |
| Elixir protocol/coordinate/ticket tests | 3 passed, including the regenerated Protobuf contract |
| Python OCR/policy/durability tests | 4 passed, including actual Tesseract extraction and mismatch |
| RentalOperations suite | 52 passed, including Mongo outbox and conservative score projection |
| RiderManager suite | 40 passed, including PostgreSQL migration and document outbox |
| Console build and contract checks | Production build and 2 contract tests passed |
| Real document path | Rust sanitization → private MinIO PNG → document.stored → worker Tesseract → document.verified passed; probe objects cleaned up |
| Telemetry burst probe | 30 seconds, 483 submissions, 29 accepted; shared maximum one accepted position/second |
| Cassandra history | Actual rows found for the fixture rider/day; TTL was below and close to 7,776,000 seconds (90 days) |
| Integrated rental trace | 22 real spans across gateway, rental, rider, motorcycle, risk and telemetry services |
| Browser acceptance | Map, trace, 200 batch attempts including 429s, and frozen marker after stopping telemetry passed; zero page errors |
| Failure isolation | With risk and telemetry stopped, rental creation returned 200 in 21.39 ms and listing returned 200; both services restored |
| Developer configuration | Core and full Tilt models evaluated successfully |
| Delivery configuration | Actionlint passed; Prometheus accepted the score-staleness alert |

Machine-readable evidence: [API trace](polyglot-api.json),
[browser checks](polyglot-browser.json), [failure isolation](polyglot-degradation.json).
The [runbook](../runbooks/polyglot-services.md) explains reproduction and identity
boundaries. Screenshots show real captured state, including asynchronous metric
export delay and explicit gateway rejections.
