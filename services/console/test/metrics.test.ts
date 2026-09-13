import assert from 'node:assert/strict';
import {test} from 'node:test';
import {METRIC_QUERIES, METRICS_TTL_MS, metricsCache} from '../lib/metrics.ts';

/** Um Prometheus de mentira que conta cada consulta recebida. */
function prometheus() {
  const queries:string[] = [];
  const query = async (promql:string) => {
    queries.push(promql);
    await new Promise(resolve => setTimeout(resolve, 5));
    return queries.length;
  };
  return {queries,query};
}

const perInterval = Object.keys(METRIC_QUERIES).length;

/**
 * O #194 pede exatamente isto: N clientes conectados produzem uma consulta por
 * métrica por intervalo, e não N. Os pedidos chegam juntos de propósito -- é o
 * caso em que guardar só o valor, e não a promessa, ainda deixaria todos
 * consultarem antes de o primeiro responder.
 */
test('a thousand tabs polling at once cost one Prometheus query per metric', async () => {
  const fake = prometheus();
  const read = metricsCache(fake.query, () => 0);

  const answers = await Promise.all(Array.from({length:1000}, () => read()));

  assert.equal(fake.queries.length, perInterval);
  assert.ok(answers.every(answer => answer === answers[0]));
});

test('the values are queried again once the interval has passed', async () => {
  const fake = prometheus();
  let clock = 0;
  const read = metricsCache(fake.query, () => clock);

  await read();
  clock = METRICS_TTL_MS - 1;
  await read();
  assert.equal(fake.queries.length, perInterval);

  clock = METRICS_TTL_MS;
  const refreshed = await read();
  assert.equal(fake.queries.length, 2 * perInterval);
  assert.equal(refreshed.measuredAt, new Date(METRICS_TTL_MS).toISOString());
});

test('a metric Prometheus cannot answer comes back as null, not as a failed panel', async () => {
  const read = metricsCache(async promql => promql === METRIC_QUERIES.queue ? null : 1, () => 0);

  const metrics = await read();

  assert.equal(metrics.queue, null);
  assert.equal(metrics.p99, 1);
  assert.equal(metrics.limited, 1);
});
