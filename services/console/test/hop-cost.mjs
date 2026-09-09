// O preço do salto extra, medido.
//
// O ADR 0014 aceita uma consequência explícita: o console chama os serviços
// ATRAVÉS do portão em vez de ao lado deles, porque a fronteira de confiança do
// ADR 0008 é uma só. E diz o que fazer com essa consequência -- "it is
// measurable: it belongs in the latency budget, not in a footnote".
//
// Este script é o número. Ele pede a MESMA leitura das duas formas, do mesmo
// lugar, na rede do fixture `projecty-load`:
//
//   console -> api-gateway -> rental-core     como a tela faz
//   console -> rental-core                    o que o salto custa a menos
//
// O caminho direto assina o envelope do ADR 0008 à mão, porque é o que o portão
// faria. Sem isso a comparação mediria "com autenticação" contra "sem", que é
// outra pergunta.
import {createHmac} from 'node:crypto';
import {writeFileSync} from 'node:fs';

const gateway = process.env.GATEWAY_URL ?? 'http://api-gateway:8090';
const direct = process.env.RENTAL_CORE_URL ?? 'http://rental-core:8200';
const audience = process.env.RENTAL_CORE_AUDIENCE ?? 'projecty.rental-core';
const keyId = process.env.GATEWAY_IDENTITY_SIGNING_KEY_ID ?? 'local-v1';
const key = process.env.GATEWAY_IDENTITY_SIGNING_KEY;
const token = process.env.ACCESS_TOKEN;
const subject = process.env.SUBJECT ?? 'rider-1';
const rounds = Number(process.env.ROUNDS ?? 40);
// O portão limita a 120 requisições por minuto por chamador. Uma rajada mais
// rápida que isso mediria a fila do limitador, não o salto -- e o 429 que ela
// produz nem chega ao upstream.
const pace = Number(process.env.PACE_MS ?? 700);
if (!key || !token) throw new Error('GATEWAY_IDENTITY_SIGNING_KEY and ACCESS_TOKEN are required');

// Uma leitura barata e real: o lote que a tela usa para resolver as motos. Um
// id que não existe responde `[]` -- o assunto aqui é o caminho, não a linha.
const path = '/api/motorcycles/batch?ids=00000000-0000-0000-0000-000000000000';

function envelope(method, pathAndQuery, roles = 'Rider') {
  const issuedAt = String(Math.floor(Date.now() / 1000));
  const canonical = ['v1', keyId, subject, roles, issuedAt, method, pathAndQuery, audience].join('\n');
  const signature = createHmac('sha256', key).update(canonical).digest('base64url');
  return {
    'x-identity-key-id': keyId,
    'x-identity-subject': subject,
    'x-identity-roles': roles,
    'x-identity-issued-at': issuedAt,
    'x-identity-signature': 'v1=' + signature,
  };
}

async function timed(url, headers) {
  const started = performance.now();
  const response = await fetch(url, {headers});
  await response.arrayBuffer();
  const elapsed = performance.now() - started;
  if (response.status === 429) {
    throw new Error(url + ' answered 429: slow the probe down (PACE_MS), it is measuring the rate limiter');
  }
  if (!response.ok) throw new Error(url + ' answered ' + response.status);
  return elapsed;
}

const throughGateway = () => timed(gateway + path, {Authorization: 'Bearer ' + token});
const straightThere = () => timed(direct + path, envelope('GET', path));

// Aquecimento: a primeira requisição paga conexão, JIT e o JWKS que o portão
// ainda não cacheou. Medir isso mediria a subida, não o salto.
for (let round = 0; round < 5; round++) {
  await throughGateway();
  await straightThere();
}

const viaGateway = [];
const viaService = [];
// Alternado, e não em blocos: uma máquina que fica lenta no meio da medição
// atinge os dois lados igualmente em vez de só o segundo.
for (let round = 0; round < rounds; round++) {
  viaGateway.push(await throughGateway());
  viaService.push(await straightThere());
  await new Promise(resolve => setTimeout(resolve, pace));
}

const percentile = (values, fraction) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * fraction))];
};
const round2 = value => Math.round(value * 100) / 100;
const summary = values => ({
  p50: round2(percentile(values, 0.5)),
  p95: round2(percentile(values, 0.95)),
  p99: round2(percentile(values, 0.99)),
});

const report = {
  measuredAt: new Date().toISOString(),
  fixture: process.env.FIXTURE ?? 'projecty-load',
  rounds,
  pace,
  path,
  viaGateway: summary(viaGateway),
  viaService: summary(viaService),
  hopCostMs: {
    p50: round2(percentile(viaGateway, 0.5) - percentile(viaService, 0.5)),
    p95: round2(percentile(viaGateway, 0.95) - percentile(viaService, 0.95)),
  },
  // Três chamadas por tela, e o salto é pago em cada uma -- mas em paralelo,
  // então a tela paga o mais lento dos três e não a soma.
  compositionCalls: 3,
  includes: [
    'verificação EdDSA contra o JWKS que o portão cacheia',
    'limitação de taxa: uma ida ao Redis por requisição',
    'assinatura do envelope do ADR 0008',
    'um salto de rede a mais',
  ],
};
console.log(JSON.stringify(report, null, 2));
if (process.env.REPORT_PATH) writeFileSync(process.env.REPORT_PATH, JSON.stringify(report, null, 2) + '\n');
