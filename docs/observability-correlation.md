# Trace, log, metric, and service-graph correlation

This runbook proves that the local stack can move in both directions between
the telemetry signals produced by one request. Run it only against the local
Compose environment.

## Generate a trace with a known ID

Start the stack and wait until every long-running service is healthy:

```powershell
docker compose up --build --detach --wait --wait-timeout 300
```

Send an expected authentication failure through the gateway. The request is
safe to repeat and should return `401`:

```powershell
$traceId = [guid]::NewGuid().ToString('N')
$parentId = [guid]::NewGuid().ToString('N').Substring(0, 16)
$headers = @{ traceparent = "00-$traceId-$parentId-01" }
$body = @{
    email = 'missing@example.test'
    password = 'wrong-password'
    audience = 'MotoHub'
} | ConvertTo-Json

try {
    Invoke-WebRequest `
        -Uri 'http://localhost:8090/api/auth/login' `
        -Method Post `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body $body `
        -UseBasicParsing
}
catch {
    if ([int]$_.Exception.Response.StatusCode -ne 401) { throw }
}

Start-Sleep -Seconds 15
$traceId
```

The delay covers the configured Collector batch and Prometheus scrape
intervals.

## Metric to trace

1. Open <http://localhost:3000> and select **ProjectY - Platform Overview**.
2. On a RED latency panel, select a data point from the request interval.
3. Select its exemplar link. Grafana must open Tempo with the same trace ID.

Prometheus stores the exemplar under the `trace_id` label. The raw API can be
used to inspect it when diagnosing the UI:

```powershell
$start = [DateTimeOffset]::UtcNow.AddMinutes(-15).ToUnixTimeSeconds()
$end = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$metric = [uri]::EscapeDataString(
    'traces_span_metrics_duration_milliseconds_bucket'
)
$exemplars = Invoke-RestMethod `
    -Uri "http://localhost:9090/api/v1/query_exemplars?query=$metric&start=$start&end=$end"
$exemplars.data.exemplars | Where-Object { $_.labels.trace_id -eq $traceId }
```

## Trace to logs

1. In Tempo, select the `auth-gate` server span.
2. Select **Logs for this span**.
3. Confirm that the generated Loki query contains both `service_name` and the
   trace ID, and that it returns the request log lines.

The datasource maps the OpenTelemetry resource attribute `service.name` to
Loki's `service_name` label. A raw equivalent is:

```powershell
$logql = [uri]::EscapeDataString(
    "{service_name=~`"api-gateway|auth-gate`"} | trace_id = `"$traceId`""
)
$logs = Invoke-RestMethod `
    -Uri "http://localhost:3100/loki/api/v1/query_range?query=$logql&limit=100"
$logs.data.result
```

## Log to trace

1. Open **Explore**, select Loki, and run the LogQL query above.
2. Expand a line and select the **TraceID / Ver trace** derived field.
3. Confirm that Tempo opens the same trace and span timeline.

To prove the error path, register a unique local rider twice while supplying a
known `traceparent` on the second request. The duplicate request returns `400`
and AuthGate emits `Failed to create rider user` at error level. This query must
return that line, and its **TraceID** link must open the corresponding trace:

```logql
{service_name="auth-gate"} | detected_level = "error" | trace_id = "<trace-id>"
```

This drill creates a test account in the local database; use unique email,
CNPJ, and CNH values or reset only the disposable local environment first.

## Service graph from real traces

The Tempo datasource obtains its service map from metrics generated from
traces. After the authentication request, the graph must include
`user -> api-gateway -> auth-gate`. A successful rider registration also flows
through the transactional outbox and must add `auth-gate -> rider-manager`.

Check the generated graph series directly with:

```powershell
$promql = [uri]::EscapeDataString(
    'sum by (client, server) (traces_service_graph_request_total)'
)
(Invoke-RestMethod `
    -Uri "http://localhost:9090/api/v1/query?query=$promql").data.result
```

The proof is complete only when all of these statements are true:

- Tempo returns the injected trace ID and the expected service spans.
- Loki returns log lines carrying that same trace ID.
- an error-level log links back to its Tempo trace.
- a latency exemplar contains the trace ID and opens Tempo.
- the service graph contains edges derived from the exercised trace path.

