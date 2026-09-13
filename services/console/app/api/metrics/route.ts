import { failure, session } from '../../../lib/server';
import { metricsCache, prometheusQuery } from '../../../lib/metrics';

// One cache per server process: every signed-in tab reads the same three
// global numbers, so they are queried once per interval, not once per tab (#194).
const globalMetrics = metricsCache(prometheusQuery);

export async function GET() {
  try {
    await session();
    return Response.json(await globalMetrics());
  } catch(error) {return failure(error)}
}
