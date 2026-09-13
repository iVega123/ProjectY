// Evidence for the drills k6 cannot measure: stopped services and Cassandra.
//
// Runs against the isolated projecty-load fixture only, after
//   powershell -File scripts/Run-LoadTest.ps1 -PrepareOnly -Polyglot
// Every drill goes through scripts/Invoke-ChaosDrill.ps1 -- the same command a
// Tilt button runs -- and is cleared in a finally, so a failed assertion never
// leaves a dependency down. Writes docs/measurements/fault-tolerance/drills.json.
import {execFileSync} from 'node:child_process';
import {mkdirSync, writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';

const project = 'projecty-load';
const base = 'http://localhost:13001';
const prometheus = 'http://127.0.0.1:19090';
const toxiproxy = 'http://127.0.0.1:18474';
const shell = process.platform === 'win32' ? 'powershell' : 'pwsh';
const compose = ['compose', '-p', project, '-f', '.env.load-compose.json'];
const docker = args => execFileSync('docker', args, {encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']});
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function drill(id, clear = false) {
  const args = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts/Invoke-ChaosDrill.ps1', id,
    '-Url', toxiproxy, '-Project', project];
  if (clear) args.push('-Clear');
  execFileSync(shell, args, {encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe']});
}

async function healthy(service, timeoutMs = 180000) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const id = docker(['ps', '-q', '--filter', `label=com.docker.compose.project=${project}`,
      '--filter', `label=com.docker.compose.service=${service}`]).trim();
    if (id && docker(['inspect', '--format', '{{.State.Health.Status}}', id]).trim() === 'healthy') return;
    await sleep(2000);
  }
  throw new Error(`${service} did not become healthy`);
}

async function metric(query) {
  const response = await fetch(`${prometheus}/api/v1/query?query=${encodeURIComponent(query)}`);
  const body = await response.json();
  return body.data.result.reduce((sum, series) => sum + Number(series.value[1]), 0);
}

// Messages ever written to a topic: the sum of its partitions' end offsets.
function topicSize(topic) {
  return docker([...compose, 'exec', '-T', 'kafka', '/opt/kafka/bin/kafka-get-offsets.sh',
    '--bootstrap-server', 'kafka:9092', '--topic', topic])
    .split(/\r?\n/).filter(line => /:\d+:\d+$/.test(line))
    .reduce((sum, line) => sum + Number(line.split(':').at(-1)), 0);
}

// Counters reach Prometheus through the OTel export interval and a scrape, so
// "it increased" is polled rather than read once.
async function increased(query, before, timeoutMs = 120000) {
  const until = Date.now() + timeoutMs;
  let value = before;
  while (Date.now() < until) {
    value = await metric(query);
    if (value > before) return value;
    await sleep(5000);
  }
  return value;
}

const {token} = JSON.parse(docker([...compose, 'exec', '-T', 'load-identity', 'node', '-e',
  "fetch('http://localhost:8080/token').then(r=>r.text()).then(t=>process.stdout.write(t))"]));
const login = await fetch(base + '/api/session', {method: 'POST',
  headers: {Origin: base, 'Content-Type': 'application/json'}, body: JSON.stringify({token})});
assert.equal(login.status, 200, await login.text());
const cookie = login.headers.get('set-cookie').split(';')[0];

async function get(path) {
  const response = await fetch(base + path, {headers: {Cookie: cookie}});
  const body = await response.json();
  return {status: response.status, body};
}

let plateCounter = Date.now() % 900;
async function createRental() {
  // The fixture seeds KAA0000-KAA9999. k6 takes the low indices and the smoke
  // tests take 8xxx and 9xxx, so the drills use 7xxx.
  const plate = 'KAA' + String(7000 + (plateCounter++ % 1000));
  const started = performance.now();
  const response = await fetch(base + '/api/rentals', {method: 'POST',
    headers: {Origin: base, Cookie: cookie, 'Content-Type': 'application/json'},
    body: JSON.stringify({plates: [plate], startDate: '2026-10-01T00:00:00Z', predictedEndDate: '2026-10-08T00:00:00Z'})});
  const action = JSON.parse((await response.text()).trim());
  return {plate, httpStatus: response.status, rentalStatus: action.status, durationMs: Math.round(performance.now() - started), action};
}

async function findRental(plate) {
  let cursor = '';
  for (let page = 0; page < 100; page++) {
    const {body} = await get('/api/session' + (cursor ? '?cursor=' + encodeURIComponent(cursor) : ''));
    const rental = body.rentals.items.find(item => item.motorcycleLicencePlate === plate);
    if (rental) return {rental, cursor};
    if (!body.rentals.nextCursor) break;
    cursor = body.rentals.nextCursor;
  }
  throw new Error('Rental not listed: ' + plate);
}

async function sendPosition(rentalId, cursor) {
  const until = Date.now() + 60000;
  let tracking;
  // The rental.started event has to reach telemetry before a ticket is useful.
  while (Date.now() < until) {
    tracking = await get('/api/tracking?rentalId=' + rentalId + '&cursor=' + encodeURIComponent(cursor));
    if (tracking.status === 200) break;
    await sleep(2000);
  }
  assert.equal(tracking.status, 200, JSON.stringify(tracking.body));
  const {socketUrl, ticket} = tracking.body;
  return new Promise((resolve, reject) => {
    let socket;
    const timeout = setTimeout(() => { socket?.close(); reject(new Error('Position not broadcast')); }, 60000);
    const open = () => {
      socket = new WebSocket(socketUrl + '?vsn=2.0.0&ticket=' + encodeURIComponent(ticket));
      socket.onopen = () => socket.send(JSON.stringify(['1', '1', 'rental:' + rentalId, 'phx_join', {}]));
      socket.onmessage = ({data}) => {
        const [, ref, , event, body] = JSON.parse(data);
        if (event === 'phx_reply' && ref === '1') {
          if (body.status !== 'ok') { socket.close(); setTimeout(open, 2000); return; }
          socket.send(JSON.stringify(['1', '2', 'rental:' + rentalId, 'position', {latitude: -3.1, longitude: -60.02}]));
        }
        if (event === 'position') { clearTimeout(timeout); socket.close(); resolve(body); }
      };
      socket.onerror = () => {};
    };
    open();
  });
}

const report = {measuredAt: new Date().toISOString(),
  commit: execFileSync('git', ['rev-parse', 'HEAD'], {encoding: 'utf8'}).trim(), drills: {}};

// Tracking stopped: the map freezes, nothing else does.
try {
  drill('service-killed');
  const rental = await createRental();
  const listing = await get('/api/session');
  assert.equal(rental.rentalStatus, 200, rental.action.detail);
  assert.equal(listing.status, 200);
  report.drills['service-killed'] = {rentalStatus: rental.rentalStatus, rentalDurationMs: rental.durationMs, listingStatus: listing.status};
} finally { drill('service-killed', true); await healthy('telemetry'); }

// Risk-pricing stopped: rentals are still priced, and the rescore they should cause waits.
//
// rental-core never calls risk-pricing; it prices from the scores and table it
// last projected. So a fallback counter is not evidence of this outage -- the
// fixture rider has no score until risk-pricing publishes one, and is counted as
// unscored whether the service is up or not. What the outage does change is that
// rental.started stops producing risk.scored. The drill shows that against a
// control taken while the service runs, then shows the backlog scored on return.
{
  async function grows(topic, before, timeoutMs = 120000) {
    const until = Date.now() + timeoutMs;
    let size = before;
    while (Date.now() < until) {
      size = topicSize(topic);
      if (size > before) return size;
      await sleep(2000);
    }
    return size;
  }

  const control = {started: topicSize('rental.started'), scored: topicSize('risk.scored')};
  const running = await createRental();
  assert.equal(running.rentalStatus, 200, running.action.detail);
  assert.ok(await grows('rental.started', control.started) > control.started, 'rental.started was not published');
  const scoredWhileRunning = await grows('risk.scored', control.scored);
  assert.ok(scoredWhileRunning > control.scored, 'risk-pricing did not rescore the rider while running');

  let outage;
  try {
    drill('risk-pricing-stopped');
    const started = topicSize('rental.started');
    const scored = topicSize('risk.scored');
    const rental = await createRental();
    assert.equal(rental.rentalStatus, 200, rental.action.detail);
    assert.ok(await grows('rental.started', started) > started, 'rental.started was not published');
    // The control rescored within this window; nothing may while the service is down.
    await sleep(30000);
    const scoredDuringOutage = topicSize('risk.scored');
    assert.equal(scoredDuringOutage, scored, 'risk.scored advanced with risk-pricing stopped');
    const {rental: listed} = await findRental(rental.plate);
    outage = {rental, listed, scored, scoredDuringOutage};
  } finally { drill('risk-pricing-stopped', true); await healthy('risk-pricing'); }

  const scoredAfterRecovery = await grows('risk.scored', outage.scoredDuringOutage);
  assert.ok(scoredAfterRecovery > outage.scoredDuringOutage, 'the rental started during the outage was never scored');
  report.drills['risk-pricing-stopped'] = {
    rentalStatus: outage.rental.rentalStatus, rentalDurationMs: outage.rental.durationMs,
    runningRentalDurationMs: running.durationMs, totalCost: outage.listed.originalTotalCost,
    riskScoredWhileRunning: {before: control.scored, after: scoredWhileRunning},
    riskScoredDuringOutage: {before: outage.scored, after30s: outage.scoredDuringOutage},
    riskScoredAfterRecovery: scoredAfterRecovery};
}

// Billing stopped: the page renders every row and says invoices are missing.
try {
  drill('billing-stopped');
  const listing = await get('/api/session');
  assert.equal(listing.status, 200);
  assert.ok(listing.body.rentals.items.length > 0, 'no rentals listed');
  assert.ok(listing.body.rentals.missing.includes('invoices'), JSON.stringify(listing.body.rentals.missing));
  report.drills['billing-stopped'] = {listingStatus: listing.status, rows: listing.body.rentals.items.length, missing: listing.body.rentals.missing};
} finally { drill('billing-stopped', true); await healthy('billing'); }

// Cassandra down: a live position is still accepted and broadcast; history fails visibly.
{
  const query = 'sum(traces_span_metrics_calls_total{span_name="cassandra.position",status_code="STATUS_CODE_ERROR"})';
  const before = await metric(query);
  const rental = await createRental();
  assert.equal(rental.rentalStatus, 200, rental.action.detail);
  const {rental: listed, cursor} = await findRental(rental.plate);
  try {
    drill('cassandra-down');
    const position = await sendPosition(listed.rentalId, cursor);
    assert.equal(position.latitude, -3.1);
    const after = await increased(query, before);
    assert.ok(after > before, 'Cassandra history failure was not visible in spanmetrics');
    report.drills['cassandra-down'] = {positionBroadcast: true, historyErrorsBefore: before, historyErrorsAfter: after};
  } finally { drill('cassandra-down', true); }
}

// Kafka down is not repeated here: its evidence is the k6 run and the drain
// measurement that Run-LoadTest.ps1 -Mode kafka-down writes.

mkdirSync('docs/measurements/fault-tolerance', {recursive: true});
writeFileSync('docs/measurements/fault-tolerance/drills.json', JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify(report, null, 2));
