import { failure, session } from '../../../lib/server';
const queries = {
  p99:'histogram_quantile(0.99, sum by (le) (rate(traces_span_metrics_duration_milliseconds_bucket{service_name="rental-operations",span_name=~"POST /?api/Rental/create"}[5m])))',
  queue:'sum(rabbitmq_detailed_queue_messages_ready)',
  limited:'sum(increase(traces_span_metrics_calls_total{service_name="api-gateway",http_response_status_code="429"}[5m]))'
};
export async function GET() {
  try {
    await session();
    const pairs = await Promise.all(Object.entries(queries).map(async ([name,query]) => {
      try {
        const response = await fetch((process.env.PROMETHEUS_URL ?? 'http://prometheus:9090')+'/api/v1/query?query='+encodeURIComponent(query),{cache:'no-store',signal:AbortSignal.timeout(4000)});
        const data = await response.json();
        const raw = data.data?.result?.[0]?.value?.[1];
        const value = raw === undefined ? NaN : Number(raw);
        return [name,Number.isFinite(value) ? value : null];
      } catch {return [name,null]}
    }));
    return Response.json({...Object.fromEntries(pairs),measuredAt:new Date().toISOString()});
  } catch(error) {return failure(error)}
}
