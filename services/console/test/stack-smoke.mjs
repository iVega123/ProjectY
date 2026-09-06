// Integration acceptance against the isolated projecty-load fixture only.
import {execFileSync} from 'node:child_process';
import {writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const compose=['compose','-p','projecty-load','-f','.env.load-compose.json'];
const docker=(args)=>execFileSync('docker',[...compose,...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']});
const base='http://localhost:13001';
const issuer=docker(['exec','-T','load-identity','node','-e',"fetch('http://localhost:8080/token').then(r=>r.text()).then(t=>process.stdout.write(t))"]);
const token=JSON.parse(issuer).token;
const login=await fetch(base+'/api/session',{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:JSON.stringify({token})});
assert.equal(login.status,200,await login.text());
const cookie=login.headers.get('set-cookie').split(';')[0];
async function get(path) {const r=await fetch(base+path,{headers:{Cookie:cookie}});const body=await r.json();assert.equal(r.status,200,JSON.stringify(body));return body}
async function create(plate) {
  const r=await fetch(base+'/api/rentals',{method:'POST',headers:{Origin:base,Cookie:cookie,'Content-Type':'application/json'},
    body:JSON.stringify({plates:[plate],startDate:'2026-10-01T00:00:00Z',predictedEndDate:'2026-10-08T00:00:00Z'})});
  assert.equal(r.status,200,await r.clone().text());
  const action=JSON.parse((await r.text()).trim());assert.equal(action.status,200,action.detail);return action;
}
const suffix=String(Date.now()%1000).padStart(3,'0');
const action=await create('KAA8'+suffix);
const session=await get('/api/session');
const rental=session.rentals.items.find(r=>r.motocycleLicencePlate===action.plate);
assert.ok(rental);
const auth=await get('/api/tracking?rentalId='+rental.rentalId);
let socket;
const position=await new Promise((resolve,reject)=>{
  const timeout=setTimeout(()=>reject(new Error('Position not received')),20000);
  const open=()=>{
    socket=new WebSocket(auth.socketUrl+'?vsn=2.0.0&ticket='+encodeURIComponent(auth.ticket));
    socket.onopen=()=>socket.send(JSON.stringify(['1','1','rental:'+rental.rentalId,'phx_join',{}]));
    socket.onmessage=({data})=>{
      const [,ref,,event,body]=JSON.parse(data);
      if(event==='phx_reply' && ref==='1') {
        if(body.status!=='ok') {socket.close();setTimeout(open,1000);return}
        socket.send(JSON.stringify(['1','2','rental:'+rental.rentalId,'position',{latitude:-3.119,longitude:-60.021}]));
      }
      if(event==='position') {clearTimeout(timeout);resolve(body)}
    };
    socket.onerror=()=>{};
  };open();
});
assert.equal(position.latitude,-3.119);
let spans=[];
for(let i=0;i<20;i++) {
  const trace=await get('/api/trace?id='+action.traceId+'&grant='+encodeURIComponent(action.grant));spans=trace.spans;
  if(spans.some(s=>s.service==='telemetry') && spans.some(s=>s.service==='risk-pricing')) break;
  await new Promise(resolve=>setTimeout(resolve,3000));
}
assert.ok(spans.some(s=>s.service==='api-gateway'),'Missing gateway trace');
assert.ok(spans.some(s=>s.service==='rental-operations'),'Missing rental trace');
assert.ok(spans.some(s=>s.service==='telemetry'),'Missing Kafka telemetry consumer trace');
assert.ok(spans.some(s=>s.service==='risk-pricing'),'Missing risk consumer trace');
const metrics=await get('/api/metrics');
const report={measuredAt:new Date().toISOString(),action:{...action,grant:undefined},rentalId:rental.rentalId,position,services:[...new Set(spans.map(s=>s.service))],spans,metrics};
writeFileSync('docs/measurements/polyglot-api.json',JSON.stringify(report,null,2)+'\n');
writeFileSync('.env.console-session.json',JSON.stringify({token,cookie,rentalId:rental.rentalId,action}));
socket.close();
console.log(JSON.stringify({integration:'passed',services:report.services,spanCount:spans.length,metrics}));
