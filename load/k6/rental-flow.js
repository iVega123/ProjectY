// Carga sobre o fluxo real de aluguel, atravessando gateway → rental-core → Cockroach.
//
// As métricas vão para o Prometheus por remote-write, então o mesmo painel do
// Grafana que mostra a saúde do sistema mostra a carga que você está aplicando.
// É essa sobreposição que torna o teste demonstrável: dá para ver o limite de
// taxa entrando em ação e o p99 subindo, na mesma tela, ao vivo.

import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";

const BASE = __ENV.BASE_URL || "http://localhost:8090";
const VUS = parseInt(__ENV.VUS || "20", 10);
const DURATION = __ENV.DURATION || "30s";

const rateLimited = new Counter("projecty_rate_limited_total");
const rentalLatency = new Trend("projecty_rental_create_ms", true);

export const options = {
  scenarios: {
    steady: {
      executor: "constant-vus",
      vus: VUS,
      duration: DURATION,
      gracefulStop: "10s",
    },
  },
  // Limiares fazem o k6 sair com código != 0 quando o sistema não cumpre o
  // acordo — o teste de carga vira um portão de CI, não só um gráfico bonito.
  thresholds: {
    "http_req_failed{expected_response:true}": ["rate<0.01"],
    "projecty_rental_create_ms": ["p(95)<800", "p(99)<2000"],
    checks: ["rate>0.95"],
  },
  discardResponseBodies: false,
};

const PLATES = ["ABC1D23", "XYZ9K88", "QRS4T56", "JKL7M01", "PQR2N45"];

function plate() {
  return PLATES[Math.floor(Math.random() * PLATES.length)];
}

function isoDaysFromNow(days) {
  return new Date(Date.now() + days * 86400000).toISOString();
}

export default function () {
  const headers = {
    "Content-Type": "application/json",
    // O gateway usa esta chave para tornar a repetição segura: reenviar a mesma
    // requisição não cria um segundo aluguel.
    "Idempotency-Key": `${__VU}-${__ITER}-${Date.now()}`,
  };

  const payload = JSON.stringify({
    licensePlate: plate(),
    startsAt: isoDaysFromNow(1),
    predictedEndsAt: isoDaysFromNow(8),
  });

  const res = http.post(`${BASE}/api/rentals`, payload, {
    headers,
    tags: { name: "POST /api/rentals" },
  });

  rentalLatency.add(res.timings.duration);

  if (res.status === 429) {
    // Não é falha: é o limite de taxa funcionando. Contabilizar à parte evita
    // que a proteção que está funcionando apareça como erro no painel.
    rateLimited.add(1);
  } else {
    check(res, {
      "aceito ou recusado por regra de negócio": (r) =>
        r.status === 201 || r.status === 200 || r.status === 409 || r.status === 422,
      "sem erro de servidor": (r) => r.status < 500,
    });
  }

  sleep(Math.random() * 0.5);
}

export function handleSummary(data) {
  const m = data.metrics;
  const line = (k) => (m[k] ? m[k].values : {});
  return {
    stdout:
      "\n" +
      "  ProjectY — resumo da carga\n" +
      "  ─────────────────────────────────────────\n" +
      `  requisições .............. ${line("http_reqs").count ?? 0}\n` +
      `  barradas pelo rate limit . ${line("projecty_rate_limited_total").count ?? 0}\n` +
      `  p95 criação de aluguel ... ${Math.round(line("projecty_rental_create_ms")["p(95)"] ?? 0)} ms\n` +
      `  p99 criação de aluguel ... ${Math.round(line("projecty_rental_create_ms")["p(99)"] ?? 0)} ms\n` +
      "\n  Painel: http://localhost:3001/d/projecty-plataforma\n\n",
  };
}
