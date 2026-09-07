import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';

export function fields(schema) {
  return [...schema.replace(/\/\/[^\n]*/g, '').matchAll(/^\s*(?:(optional|repeated)\s+)?(\w+)\s+(\w+)\s*=\s*(\d+)\s*;/gm)]
    .map(([, presence, type, name, number]) => ({presence, type, name, number: Number(number)}));
}

export function validate(schema, previous = schema) {
  const current = fields(schema);
  assert(current.length, 'Schema has no fields');
  assert.equal(new Set(current.map(f => f.number)).size, current.length, 'Duplicate field number');
  for (const field of current) {
    assert(['optional', 'repeated'].includes(field.presence), `${field.name}: explicit presence required`);
    if (/(minor|cost|amount|price|total)/.test(field.name)) assert.equal(field.type, 'int64', `${field.name}: money must use int64 minor units`);
  }
  for (const match of schema.matchAll(/enum\s+\w+\s*\{([^}]+)\}/g)) {
    assert(/\w+_UNSPECIFIED\s*=\s*0\s*;/.test(match[1]), 'Enum requires _UNSPECIFIED = 0');
  }
  for (const old of fields(previous)) {
    const next = current.find(f => f.number === old.number);
    if (next) {
      assert.equal(next.name, old.name, `Field ${old.number} reused or renamed`);
      assert.equal(next.type, old.type, `Field ${old.number} changed type`);
      assert.equal(next.presence, old.presence, `Field ${old.number} changed presence`);
    } else {
      assert(new RegExp(`reserved\\s+[^;]*\\b${old.number}\\b[^;]*;`).test(schema), `Removed field ${old.number} must be reserved`);
    }
  }
}

export async function registryRequest(base, path, method = 'GET', body) {
  const response = await fetch(base + path, {method,
    headers: {'Content-Type': 'application/vnd.schemaregistry.v1+json'},
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(10000)});
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  return response.json();
}

export async function checkRegistry(base, baselineRef) {
  const topics = JSON.parse(await readFile(new URL('./topics.json', import.meta.url)));
  let governedTopics = {};
  if (baselineRef) {
    execFileSync('git', ['rev-parse', '--verify', baselineRef], {stdio: 'pipe'});
    if (execFileSync('git', ['ls-tree', '--name-only', baselineRef, 'contracts/topics.json'], {encoding: 'utf8'}).trim()) {
      governedTopics = JSON.parse(execFileSync('git', ['show', `${baselineRef}:contracts/topics.json`], {encoding: 'utf8'}));
    }
    for (const topic of Object.keys(governedTopics)) assert(topics[topic], `Retained topic contract removed: ${topic}`);
  }
  for (const [topic, contract] of Object.entries(topics)) {
    const expected = topic.startsWith('rental.') || topic.startsWith('motorcycle.') ? 'motorcycle_id'
      : topic.startsWith('invoice.') ? 'rental_id' : topic === 'pricing.updated' ? 'currency' : 'rider_id';
    assert.equal(contract.key, expected, `${topic}: immutable partition key required`);
    const schema = await readFile(new URL('./events/' + contract.schema, import.meta.url), 'utf8');
    let previous = schema;
    if (baselineRef) {
      const path = `contracts/events/${governedTopics[topic]?.schema || contract.schema}`;
      if (execFileSync('git', ['ls-tree', '--name-only', baselineRef, path], {encoding: 'utf8'}).trim()) {
        previous = execFileSync('git', ['show', `${baselineRef}:${path}`], {encoding: 'utf8'});
      }
    }
    validate(schema, previous);
    const subject = encodeURIComponent(topic + '-value');
    await registryRequest(base, '/config/' + subject, 'PUT', {compatibility: 'FULL'});
    // Registry adoption establishes the first governed version. The old raw
    // Protobuf still passes the field-preservation check above. Once governed,
    // every change must also pass the registry against the actual base version.
    await registryRequest(base, `/subjects/${subject}/versions`, 'POST', {schemaType: 'PROTOBUF', schema: governedTopics[topic] ? previous : schema});
    const compatible = await registryRequest(base, `/compatibility/subjects/${subject}/versions/latest`, 'POST', {schemaType: 'PROTOBUF', schema});
    assert.equal(compatible.is_compatible, true, `${topic}: registry rejected proposed schema`);
    await registryRequest(base, `/subjects/${subject}/versions`, 'POST', {schemaType: 'PROTOBUF', schema});
    // A real negative control: proves Protobuf checking is active, not a no-op.
    const broken = schema.replace('optional string event_id = 1;', 'optional int64 event_id = 1;');
    assert.notEqual(broken, schema);
    const rejected = await registryRequest(base, `/compatibility/subjects/${subject}/versions/latest`, 'POST', {schemaType: 'PROTOBUF', schema: broken});
    assert.equal(rejected.is_compatible, false, `${topic}: registry accepted the incompatible canary`);
  }
  console.log('PASS: FULL Protobuf compatibility, negative controls, presence, money and immutable keys');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await checkRegistry(process.env.SCHEMA_REGISTRY_URL || 'http://localhost:18081/apis/ccompat/v7', process.env.CONTRACT_BASE_REF);
}
