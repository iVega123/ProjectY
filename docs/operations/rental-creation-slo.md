# Rental creation SLO and error-budget policy

## Objectives

The service-level indicator includes every server span named
`POST api/Rental/create` (with or without a leading slash in the route name)
from `rental-operations`.

| Objective | Good event | Target | Window | Error budget |
|---|---|---:|---:|---:|
| Availability | HTTP response status is 2xx | 99.5% | Rolling 30 days | 0.5% non-2xx responses |
| Latency | Request completes within 2 seconds | 99% | Rolling 30 days | 1% slower responses |

The latency boundary is an explicit spanmetrics histogram bucket, so the SLI is
a good-event ratio rather than an estimate derived from a percentile. Empty or
missing telemetry is unknown, not 100% availability. At low traffic, the team
must inspect the event counts shown beside the SLI before interpreting a burn
rate.

For example, 10,000 eligible rental creation attempts provide an availability
budget of 50 non-2xx responses and a latency budget of 100 responses over two
seconds. The dashboard reports the remaining fraction of the availability
budget rather than presenting a calendar-month uptime percentage.

## Paging policy

Prometheus evaluates two multi-window pairs for each objective. The fast pair
pages when both the five-minute and one-hour bad-event ratios exceed a 14.4x
burn rate. The sustained pair pages when both the 30-minute and six-hour ratios
exceed a 6x burn rate. A short window can never page by itself. Availability
burn is divided by its 0.5% budget; latency burn is divided by its 1% budget.

The Rentals on-call engineer owns the page and incident response. When either
budget reaches zero, the Rentals engineering lead freezes feature releases and
risky configuration changes that can affect rental creation; reliability,
security, rollback, and incident-mitigation changes remain allowed. The
engineering lead and product owner jointly approve any other exception and
record the reason in the incident. They may lift the freeze only after the
active burn is below 1x for 24 hours, a root cause or bounded mitigation has an
owner, and the rolling-budget projection is positive. Below 25% remaining, any
change to the path requires an explicit reliability-impact review even if no
page is active.

The rules live in
[`deploy/observability/rules/rental-creation-slo.yml`](../../deploy/observability/rules/rental-creation-slo.yml)
and are unit-tested with `promtool`. The `severity: page`, `team: rentals`, and
`slo` labels are the routing contract for an Alertmanager integration; the
local stack intentionally exposes rule state without contacting people.

## Availability burn drill

The drill sends real, authenticated requests to the current Rental Operations
endpoint. It signs the same short-lived internal identity envelope used by the
gateway and deliberately supplies an inverted rental period. The controller
returns `400`, no rental is written, and the application exports the resulting
server spans through the normal OTLP path. This tests the SLO failure numerator
without weakening authentication or relying on a future identity issuer.

Start the normal stack, then run the opt-in load profile:

```powershell
docker compose --env-file .env up --build --detach --wait --wait-timeout 300
$env:K6_VUS = '5'
$env:K6_DURATION = '6m'
docker compose --env-file .env --profile load run --rm k6-slo-drill
```

After the Collector batch and Prometheus scrape intervals have elapsed, open
**ProjectY — Rental Creation SLO** in Grafana. The failure series must increase,
the five-minute availability burn rate must approach `200x` (100% bad events
divided by the 0.5% budget), and Prometheus must show the availability alert as
pending or firing once both windows have crossed for the configured duration.

The same facts can be checked through the Prometheus API:

```powershell
$queries = @(
    'projecty:rental_creation_availability_bad_event_ratio:rate5m / 0.005',
    'projecty:rental_creation_availability_bad_event_ratio:rate1h / 0.005',
    'ALERTS{alertname="ProjectYRentalCreationAvailabilityBudgetFastBurn"}'
)

foreach ($query in $queries) {
    $encoded = [uri]::EscapeDataString($query)
    Invoke-RestMethod "http://localhost:9090/api/v1/query?query=$encoded"
}
```

Record the dashboard time range, k6 VUs and duration, both burn-rate values,
and the alert state in the issue or pull request. Stop the local stack after
the evidence has been captured:

```powershell
docker compose --env-file .env down
```

## Rule verification

CI uses the Prometheus version pinned by Compose. The equivalent local commands
are:

```powershell
docker run --rm --entrypoint promtool `
  --volume "${PWD}/deploy/observability:/observability:ro" `
  prom/prometheus:v3.14.0 `
  check rules /observability/rules/rental-creation-slo.yml

docker run --rm --entrypoint promtool `
  --volume "${PWD}/deploy/observability:/observability:ro" `
  prom/prometheus:v3.14.0 `
  test rules /observability/tests/rental-creation-slo.test.yml
```
