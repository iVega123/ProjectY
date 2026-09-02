import http from "k6/http";
import { check, fail, sleep } from "k6";
import { Gauge, Rate, Trend } from "k6/metrics";

const BASE_URL = __ENV.MOTO_HUB_URL || "http://localhost:8090";
const ADMIN_TOKEN = __ENV.ADMIN_TOKEN || "";
const BASELINE_RECORDS = parseInt(__ENV.BASELINE_RECORDS || "100", 10);
const GROWTH_RECORDS = parseInt(__ENV.GROWTH_RECORDS || "2000", 10);
const BASELINE_SAMPLES = parseInt(__ENV.BASELINE_SAMPLES || "40", 10);
const MAX_P99_GROWTH_RATIO = parseFloat(__ENV.MAX_P99_GROWTH_RATIO || "1.50");
const MIN_JITTER_BUDGET_MS = parseFloat(__ENV.MIN_JITTER_BUDGET_MS || "20");

const baselineP99 = new Gauge("pagination_baseline_p99_ms");
const grownLatency = new Trend("pagination_grown_dataset_ms", true);
const flatAtP99 = new Rate("pagination_flat_at_p99");

export const options = {
  scenarios: {
    grownDataset: {
      executor: "constant-vus",
      vus: parseInt(__ENV.VUS || "10", 10),
      duration: __ENV.DURATION || "30s",
      gracefulStop: "5s",
    },
  },
  thresholds: {
    checks: ["rate>0.99"],
    pagination_flat_at_p99: ["rate>=0.99"],
  },
  discardResponseBodies: false,
  setupTimeout: __ENV.SETUP_TIMEOUT || "10m",
};

function headers(idempotencyKey) {
  const result = {
    Authorization: `Bearer ${ADMIN_TOKEN}`,
    "Content-Type": "application/json",
  };
  if (idempotencyKey) {
    result["Idempotency-Key"] = idempotencyKey;
  }
  return result;
}

function seed(count, prefix) {
  const batchSize = 20;
  for (let offset = 0; offset < count; offset += batchSize) {
    const requests = [];
    for (let index = offset; index < Math.min(offset + batchSize, count); index += 1) {
      requests.push({
        method: "POST",
        url: `${BASE_URL}/api/Motorcycles`,
        body: JSON.stringify({
          year: 2026,
          model: "Pagination growth proof",
          licensePlate: `${prefix}-${index}`,
        }),
        params: {
          headers: headers(`${prefix}-${index}`),
          tags: { name: "seed motorcycles" },
        },
      });
    }

    const responses = http.batch(requests);
    for (const response of responses) {
      if (response.status !== 200 && response.status !== 409) {
        fail(`Seed failed with HTTP ${response.status}: ${response.body}`);
      }
    }
  }
}

function sampleP99(sampleCount) {
  const durations = [];
  for (let index = 0; index < sampleCount; index += 1) {
    const response = http.get(`${BASE_URL}/api/Motorcycles?pageSize=100`, {
      headers: headers(),
      tags: { name: "baseline motorcycle page" },
    });
    if (response.status !== 200) {
      fail(`Baseline listing failed with HTTP ${response.status}: ${response.body}`);
    }
    durations.push(response.timings.duration);
  }

  durations.sort((left, right) => left - right);
  return durations[Math.max(0, Math.ceil(durations.length * 0.99) - 1)];
}

export function setup() {
  if (!ADMIN_TOKEN) {
    fail("ADMIN_TOKEN is required to seed and query MotoHub.");
  }

  const runId = `${Date.now()}-${Math.floor(Math.random() * 100000)}`;
  seed(BASELINE_RECORDS, `BASE-${runId}`);
  const measuredBaselineP99 = sampleP99(BASELINE_SAMPLES);
  seed(GROWTH_RECORDS, `GROW-${runId}`);

  return {
    measuredBaselineP99,
    allowedDuration: Math.max(
      measuredBaselineP99 * MAX_P99_GROWTH_RATIO,
      measuredBaselineP99 + MIN_JITTER_BUDGET_MS,
    ),
  };
}

export default function (data) {
  const response = http.get(`${BASE_URL}/api/Motorcycles?pageSize=100`, {
    headers: headers(),
    tags: { name: "grown motorcycle page" },
  });

  baselineP99.add(data.measuredBaselineP99);
  grownLatency.add(response.timings.duration);
  flatAtP99.add(response.status === 200 && response.timings.duration <= data.allowedDuration);
  check(response, {
    "listing succeeds": (result) => result.status === 200,
    "page remains server bounded": (result) => {
      try {
        return JSON.parse(result.body).items.length <= 100;
      } catch (_) {
        return false;
      }
    },
  });
  sleep(0.05);
}

export function handleSummary(data) {
  const baseline = data.metrics.pagination_baseline_p99_ms?.values?.value || 0;
  const grown = data.metrics.pagination_grown_dataset_ms?.values?.["p(99)"] || 0;
  const ratio = baseline > 0 ? grown / baseline : 0;
  return {
    stdout:
      "\nProjectY pagination growth proof\n" +
      `baseline p99: ${baseline.toFixed(2)} ms\n` +
      `grown dataset p99: ${grown.toFixed(2)} ms\n` +
      `p99 growth ratio: ${ratio.toFixed(2)}x (limit ${MAX_P99_GROWTH_RATIO.toFixed(2)}x plus jitter budget)\n\n`,
  };
}
