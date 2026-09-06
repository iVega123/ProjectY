import { createHmac } from 'node:crypto';
import assert from 'node:assert/strict';

const rental = process.env.TRACKING_RENTAL_ID ?? 'integration-rental';
const rider = process.env.TRACKING_RIDER_ID ?? 'integration-rider';
const payload = Buffer.from(JSON.stringify({rental_id:rental, rider_id:rider,
  exp:Math.floor(Date.now()/1000)+60})).toString('base64url');
const key = process.env.TELEMETRY_TICKET_KEY ?? process.env.GATEWAY_IDENTITY_SIGNING_KEY;
assert.ok(key, 'A local tracking ticket key is required');
const ticket = payload + '.' + createHmac('sha256', key).update(payload).digest('base64url');
const socket = new WebSocket(`ws://localhost:4000/socket/websocket?vsn=2.0.0&ticket=${ticket}`);
const timeout = setTimeout(() => {console.error('No real position received'); process.exit(1)}, 15000);
socket.onopen = () => socket.send(JSON.stringify(['1','1',`rental:${rental}`,'phx_join',{}]));
socket.onmessage = ({data}) => {
  const [,ref,,event,body] = JSON.parse(data);
  if(event === 'phx_reply' && ref === '1') {
    assert.equal(body.status, 'ok', JSON.stringify(body));
    socket.send(JSON.stringify(['1','2',`rental:${rental}`,'position',{latitude:-3.119,longitude:-60.021}]));
  }
  if(event === 'position') {
    assert.equal(body.latitude, -3.119);
    assert.ok(Math.abs(Date.now()-body.recorded_at) < 15000);
    console.log(JSON.stringify({websocket:'passed', rental, position:body}));
    clearTimeout(timeout); socket.close();
  }
};
