import {readFile} from 'node:fs/promises';
import {registryRequest, validate} from './check.mjs';
const base = process.env.SCHEMA_REGISTRY_URL || 'http://schema-registry:8080/apis/ccompat/v7';
const topics = JSON.parse(await readFile(new URL('./topics.json', import.meta.url)));
for (const [topic, {schema: file}] of Object.entries(topics)) {
  const schema = await readFile(new URL('./events/' + file, import.meta.url), 'utf8');
  validate(schema);
  const subject = encodeURIComponent(topic + '-value');
  await registryRequest(base, '/config/' + subject, 'PUT', {compatibility: 'FULL'});
  const result = await registryRequest(base, `/subjects/${subject}/versions`, 'POST', {schemaType: 'PROTOBUF', schema});
  console.log(`${topic}: registered schema ${result.id} under FULL compatibility`);
}
