import { cookies } from 'next/headers';
import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import type { Attempt, RentalPage } from './types';

export const gateway = process.env.GATEWAY_URL ?? 'http://api-gateway:8090';
export const origin = process.env.CONSOLE_ORIGIN ?? 'http://localhost:3001';
export function sameOrigin(request: Request) {
  if (request.headers.get('origin') !== origin) throw new Error('Origin rejected');
}
export async function token() {
  const value = (await cookies()).get('projecty-session')?.value;
  if (!value) throw new Error('Sign in to operate the stack.');
  return value;
}
export async function upstream(path: string, value: string, init: RequestInit = {}) {
  return fetch(gateway + path, {...init, cache:'no-store', signal:AbortSignal.timeout(10000),
    headers:{'Authorization':'Bearer '+value, ...init.headers}});
}
export async function session(cursor = ''): Promise<{token:string; userId:string; rentals:RentalPage}> {
  if(cursor.length > 2048) throw new Error('Invalid rental cursor');
  const value = await token();
  const result = await upstream('/api/Rental/user?pageSize=100'+(cursor?'&cursor='+encodeURIComponent(cursor):''), value);
  if (!result.ok) throw new Error(result.status === 429 ? 'Gateway rate limit; retry shortly.' : 'Session unavailable. Sign in again.');
  // Claims are read only after the gateway has validated signature, issuer, audience and revocation.
  const claims = JSON.parse(Buffer.from(value.split('.')[1], 'base64url').toString());
  if (typeof claims.sub !== 'string') throw new Error('Session has no subject');
  return {token:value, userId:claims.sub, rentals:await result.json()};
}
function signature(payload: string, domain: string) {
  const key = process.env.TELEMETRY_TICKET_KEY;
  if (!key || key.length < 32) throw new Error('Console signing key unavailable');
  return createHmac('sha256', key).update(domain + payload).digest('base64url');
}
export function ticket(rentalId:string, riderId:string) {
  const payload = Buffer.from(JSON.stringify({rental_id:rentalId, rider_id:riderId, exp:Math.floor(Date.now()/1000)+120})).toString('base64url');
  return payload+'.'+signature(payload, '');
}
export function grant(traceId:string, userId:string) {
  const payload = Buffer.from(JSON.stringify({traceId,userId,exp:Math.floor(Date.now()/1000)+3600})).toString('base64url');
  return payload+'.'+signature(payload, 'console-trace:');
}
export function verifyGrant(value:string, traceId:string, userId:string) {
  const [payload, mac] = value.split('.');
  if (!payload || !mac) return false;
  const expected = Buffer.from(signature(payload, 'console-trace:'));
  const received = Buffer.from(mac);
  if (expected.length !== received.length || !timingSafeEqual(expected, received)) return false;
  const claim = JSON.parse(Buffer.from(payload,'base64url').toString());
  return claim.traceId === traceId && claim.userId === userId && claim.exp > Date.now()/1000;
}
// A API de aluguel passou a referenciar a moto pelo id, e uma placa continua
// sendo o que a pessoa sabe de cor. Resolver uma na outra é trabalho de BFF --
// exatamente o que o #138 diz que o console faz -- e não da API de escrita, que
// perderia a referência estável se voltasse a aceitar placa.
//
// A busca devolve a falha em vez de lançá-la, porque essas falhas são resultado
// e não transporte: 404 quando a placa não existe, 429 quando o portão limita.
// Colapsar as duas num 503 esconderia o erro real de quem digitou a placa, e
// faria o gerador de carga contar menos limitações do que houve -- e esse é um
// dos números que o console mostra na tela.
type Lookup = {id:string} | {status:number; detail:string};
async function motorcycleId(value:string, plate:string): Promise<Lookup> {
  const response = await upstream('/api/motorcycles/'+encodeURIComponent(plate), value);
  if(!response.ok) return {status:response.status,
    detail:(await response.text()).slice(0,500) || 'No motorcycle with plate '+plate};
  const motorcycle = await response.json();
  return typeof motorcycle?.id === 'string' && motorcycle.id
    ? {id:motorcycle.id}
    : {status:502, detail:'Motorcycle '+plate+' came back without an id'};
}
export async function createRental(value:string, userId:string, plate:string, date:object, index:number): Promise<Attempt> {
  const traceId = randomBytes(16).toString('hex');
  const start = performance.now();
  try {
    const lookup = await motorcycleId(value,plate);
    if(!('id' in lookup)) return {index,plate,status:lookup.status,duration:performance.now()-start,
      traceId,grant:grant(traceId,userId),detail:lookup.detail};
    const response = await upstream('/api/Rental/create', value, {method:'POST',
      headers:{'Content-Type':'application/json', 'Idempotency-Key':randomBytes(16).toString('hex'),
        traceparent:`00-${traceId}-${randomBytes(8).toString('hex')}-01`},
      body:JSON.stringify({motorcycleId:lookup.id,...date})});
    return {index,plate,status:response.status,duration:performance.now()-start,traceId,
      grant:grant(traceId,userId), detail:(await response.text()).slice(0,500)};
  } catch {
    return {index,plate,status:503,duration:performance.now()-start,traceId,grant:grant(traceId,userId),detail:'Gateway unavailable or timed out'};
  }
}
export function failure(error:unknown) {
  return Response.json({error:error instanceof Error ? error.message : 'Service unavailable'}, {status:400});
}
