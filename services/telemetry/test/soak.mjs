import {createHmac} from 'node:crypto';
import assert from 'node:assert/strict';
const seconds=Number(process.env.SOAK_SECONDS ?? 30);
assert.ok(seconds>=5 && seconds<=280,'SOAK_SECONDS must fit within the five-minute tracking ticket (5–280 seconds)');
const rental=process.env.TRACKING_RENTAL_ID ?? 'integration-rental';
const rider=process.env.TRACKING_RIDER_ID ?? 'integration-rider';
const key=process.env.TELEMETRY_TICKET_KEY ?? process.env.GATEWAY_IDENTITY_SIGNING_KEY;
const payload=Buffer.from(JSON.stringify({rental_id:rental,rider_id:rider,exp:Math.floor(Date.now()/1000)+Math.min(300,seconds+20)})).toString('base64url');
const ticket=payload+'.'+createHmac('sha256',key).update(payload).digest('base64url');
const socket=new WebSocket((process.env.TRACKING_URL ?? 'ws://localhost:4000/socket/websocket')+'?vsn=2.0.0&ticket='+ticket);
let sent=0,accepted=0,last=0,interval;
const watchdog=setTimeout(()=>{console.error('soak timed out');process.exit(1)},(seconds+15)*1000);
socket.onopen=()=>socket.send(JSON.stringify(['1','1','rental:'+rental,'phx_join',{}]));
socket.onmessage=({data})=>{
  const [,ref,,event,body]=JSON.parse(data);
  if(event==='phx_reply' && ref==='1') {
    assert.equal(body.status,'ok');
    interval=setInterval(()=>socket.send(JSON.stringify(['1',String(++sent+1),'rental:'+rental,'position',{latitude:-3.119,longitude:-60.021}])),50);
    setTimeout(()=>{
      clearInterval(interval);clearTimeout(watchdog);socket.close();
      assert.ok(accepted>=Math.floor(seconds*.7),`only ${accepted} positions accepted`);
      assert.ok(accepted<=seconds+1,`rate bound exceeded: ${accepted}`);
      console.log(JSON.stringify({soak:'passed',seconds,sent,accepted,lastRecordedAt:last,maxRowsPerRiderDay:86400}));
    },seconds*1000);
  }
  if(event==='position') {assert.ok(body.recorded_at>last);last=body.recorded_at;accepted++}
};
