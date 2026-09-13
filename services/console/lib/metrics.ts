// Os três números do painel são globais: p99 de criação, fila e 429. Nada neles
// depende de quem pergunta, e cada aba aberta pergunta a cada 10 s. Sem cache,
// N abas custavam N consultas ao Prometheus por intervalo para devolver a mesma
// resposta a todo mundo (#194). Com ele, custam uma.
//
// A promessa em voo é o que fica guardado, e não só o valor: pedidos que chegam
// juntos esperam a mesma consulta em vez de dispararem as suas antes de a
// primeira responder.
export const METRICS_TTL_MS = 10000;

export const METRIC_QUERIES = {
  p99:'histogram_quantile(0.99, sum by (le) (rate(traces_span_metrics_duration_milliseconds_bucket{service_name="rental-core",span_name=~"POST /?api/Rental/create"}[5m])))',
  queue:'sum(rabbitmq_detailed_queue_messages_ready)',
  limited:'sum(increase(traces_span_metrics_calls_total{service_name="api-gateway",http_response_status_code="429"}[5m]))'
} as const;

export type MetricName = keyof typeof METRIC_QUERIES;
export type GlobalMetrics = Record<MetricName, number | null> & {measuredAt:string};
export type MetricQuery = (promql:string) => Promise<number | null>;

/** Uma consulta instantânea. Falha vira `null`: um número faltando não derruba o painel. */
export async function prometheusQuery(promql:string): Promise<number | null> {
  try {
    const response = await fetch((process.env.PROMETHEUS_URL ?? 'http://prometheus:9090')+'/api/v1/query?query='+encodeURIComponent(promql),
      {cache:'no-store',signal:AbortSignal.timeout(4000)});
    const data = await response.json();
    const raw = data.data?.result?.[0]?.value?.[1];
    const value = raw === undefined ? NaN : Number(raw);
    return Number.isFinite(value) ? value : null;
  } catch {return null}
}

export function metricsCache(query:MetricQuery, now:() => number = Date.now, ttlMs = METRICS_TTL_MS) {
  let cached: {at:number; value:Promise<GlobalMetrics>} | undefined;
  return (): Promise<GlobalMetrics> => {
    const at = now();
    if (!cached || at - cached.at >= ttlMs) {
      const names = Object.keys(METRIC_QUERIES) as MetricName[];
      cached = {at, value:Promise.all(names.map(async name => [name, await query(METRIC_QUERIES[name])] as const))
        .then(pairs => ({...Object.fromEntries(pairs), measuredAt:new Date(at).toISOString()}) as GlobalMetrics)};
    }
    return cached.value;
  };
}
