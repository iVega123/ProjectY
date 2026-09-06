import {execFileSync} from 'node:child_process';
import {writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';
const compose=['compose','-p','projecty-load','-f','.env.load-compose.json'];
const docker=args=>execFileSync('docker',[...compose,...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']});
const {token}=JSON.parse(docker(['exec','-T','load-identity','node','-e',"fetch('http://localhost:8080/token').then(r=>r.text()).then(t=>process.stdout.write(t))"]));
const base='http://localhost:13001';
const login=await fetch(base+'/api/session',{method:'POST',headers:{Origin:base,'Content-Type':'application/json'},body:JSON.stringify({token})});
assert.equal(login.status,200);
const cookie=login.headers.get('set-cookie').split(';')[0];
try {
  docker(['stop','risk-pricing','telemetry']);
  const plate='KAA9'+String(Date.now()%1000).padStart(3,'0');
  const response=await fetch(base+'/api/rentals',{method:'POST',headers:{Origin:base,Cookie:cookie,'Content-Type':'application/json'},body:JSON.stringify({plates:[plate],startDate:'2026-10-01T00:00:00Z',predictedEndDate:'2026-10-08T00:00:00Z'})});
  assert.equal(response.status,200);
  const result=JSON.parse((await response.text()).trim());assert.equal(result.status,200,result.detail);
  const listing=await fetch(base+'/api/session',{headers:{Cookie:cookie}});assert.equal(listing.status,200);
  const report={measuredAt:new Date().toISOString(),stopped:['risk-pricing','telemetry'],rentalStatus:result.status,rentalDurationMs:result.duration,listingStatus:listing.status};
  writeFileSync('docs/measurements/polyglot-degradation.json',JSON.stringify(report,null,2)+'\n');
  console.log(JSON.stringify(report));
} finally {docker(['start','risk-pricing','telemetry'])}
