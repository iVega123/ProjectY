import http from "k6/http";
import { check, sleep, fail } from "k6";
import execution from "k6/execution";
import { Counter, Trend } from "k6/metrics";

const base = __ENV.BASE_URL || "http://api-gateway:8090";
const accepted = new Counter("projecty_rentals_created");
const limited = new Counter("projecty_rate_limited");
const latency = new Trend("projecty_rental_create_ms");
const refusalLatency = new Trend("projecty_rental_refusal_ms");
const expected = http.expectedStatuses(200, 201, 429);

export const options = {
  scenarios: { steady: { executor: "constant-vus", vus: Number(__ENV.VUS || 5),
    duration: __ENV.DURATION || "30s", gracefulStop: "5s" } },
  thresholds: {
    "http_req_failed{name:rental-create}": ["rate<0.01"],
    projecty_rental_create_ms: ["p(95)<800", "p(99)<2000"],
    projecty_rentals_created: ["count>=100"],
    checks: ["rate>0.99"],
  },
  summaryTrendStats: ["avg", "min", "med", "max", "p(95)", "p(99)"],
};

// The same injections as deploy/chaos/drills.json, applied for the length of the run.
const drills = {
  baseline: [],
  "slow-db": [{ proxy: "cockroachdb", type: "latency", attributes: { latency: 500, jitter: 0 } }],
  "db-down": [{ proxy: "cockroachdb", type: "timeout", attributes: { timeout: 0 } }],
  "rabbit-down": [{ proxy: "rabbitmq", type: "timeout", attributes: { timeout: 0 } }],
  "kafka-down": [{ proxy: "kafka", type: "timeout", attributes: { timeout: 0 } }],
};

// A fixture semeia a moto n com um id determinístico (load/fixtures/seed-rental-core.sql).
// O benchmark o calcula em vez de perguntar: um GET a mais por iteração mediria
// a busca da moto junto com a criação do aluguel, que não é o que este teste afirma.
function motorcycleId(index) {
  return "00000000-0000-4000-8000-" + String(index).padStart(12, "0");
}

function create(token, motorcycle, key, name) {
  return http.post(base + "/api/Rental/create", JSON.stringify({
    motorcycleId: motorcycle,
    startDate: "2026-10-01T00:00:00Z",
    predictedEndDate: "2026-10-08T00:00:00Z",
  }), {
    headers: { Authorization: "Bearer " + token, "Content-Type": "application/json", "Idempotency-Key": key },
    responseCallback: expected, timeout: "5s", tags: { name },
  });
}

let vuCredentials;
function fetchCredentials() {
  const response = http.get((__ENV.IDENTITY_URL || "http://load-identity:8080") + "/token", { tags: { name: "fixture-token" } });
  if (response.status !== 200) fail("Test identity fixture unavailable");
  const credentials = response.json();
  if (!credentials.token || !Number.isFinite(credentials.expiresAt) || credentials.expiresAt * 1000 <= Date.now() + 30000)
    fail("Test identity fixture returned invalid or expiring credentials");
  return credentials;
}

export function setup() {
  const credentials = fetchCredentials();
  const token = credentials.token;
  const run = Date.now().toString();
  const readyUntil = Date.now() + 40000;
  let warmup;
  do {
    warmup = create(token, motorcycleId(9999), "warmup-" + run, "warmup");
    if (warmup.status === 200 || warmup.status === 201) break;
    sleep(1); // Allow a previously opened breaker to enter half-open after a cleared drill.
  } while (Date.now() < readyUntil);
  if (warmup.status !== 200 && warmup.status !== 201) fail("Rental warmup failed: " + warmup.status + " " + warmup.body);
  const mode = __ENV.MODE || "baseline";
  if (!(mode in drills)) fail("Unknown load mode: " + mode);
  drills[mode].forEach((toxic, index) => {
    const injection = http.post("http://toxiproxy:8474/proxies/" + toxic.proxy + "/toxics",
      JSON.stringify({ name: "load-drill-" + index, type: toxic.type,
        stream: "downstream", toxicity: 1, attributes: toxic.attributes }),
      { headers: { "Content-Type": "application/json" }, tags: { name: "chaos-injection" } });
    if (injection.status !== 200) fail("Could not inject benchmark fault: " + injection.status);
  });
  return { credentials, run };
}

export default function(data) {
  const index = execution.scenario.iterationInTest;
  if (index >= 9999) fail("Fixture capacity exceeded; increase the seeded range before increasing the workload");
  // Each VU retains renewed credentials; setup time is included in the expiry check.
  if (!vuCredentials) vuCredentials = data.credentials;
  if (Date.now() >= vuCredentials.expiresAt * 1000 - 30000) vuCredentials = fetchCredentials();
  const response = create(vuCredentials.token, motorcycleId(index),
    data.run + "-" + index, "rental-create");
  const created = response.status === 200 || response.status === 201;
  accepted.add(created ? 1 : 0);
  if (created) latency.add(response.timings.duration);
  limited.add(response.status === 429 ? 1 : 0);
  if (response.status === 503) refusalLatency.add(response.timings.duration);
  check(response, { "created or explicitly rate limited": () => created || response.status === 429 });
  sleep(0.1);
}

export function handleSummary(data) {
  const summary = {
    measuredAt: new Date().toISOString(), vus: Number(__ENV.VUS || 5),
    duration: __ENV.DURATION || "30s", mode: __ENV.MODE || "baseline",
    metrics: data.metrics,
  };
  return { stdout: JSON.stringify(summary, null, 2) + "\n",
    [__ENV.SUMMARY_PATH || "/results/baseline.json"]: JSON.stringify(summary, null, 2) + "\n" };
}

export function teardown() {
  (drills[__ENV.MODE || "baseline"] || []).forEach((toxic, index) => {
    const response = http.del("http://toxiproxy:8474/proxies/" + toxic.proxy + "/toxics/load-drill-" + index,
      null, { tags: { name: "chaos-clear" } });
    check(response, { "benchmark toxic cleared": r => r.status === 204 });
  });
}
