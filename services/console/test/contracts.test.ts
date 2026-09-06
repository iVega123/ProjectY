import assert from 'node:assert/strict';
import {test} from 'node:test';
import {plates,dates,traceSpans} from '../lib/contracts.ts';
test('batch bounds and destination input reject unbounded or arbitrary targets',()=>{
  assert.deepEqual(plates(['ABC1234','ABC1D23']),['ABC1234','ABC1D23']);
  assert.throws(()=>plates(Array(101).fill('ABC1234')));
  assert.throws(()=>plates(['http://internal/admin']));
  assert.throws(()=>dates('bad','2026-10-10'));
});
test('OTLP trace keeps actual duration, service and parent relationship',()=>{
  const result=traceSpans({batches:[{resource:{attributes:[{key:'service.name',value:{stringValue:'telemetry'}}]},scopeSpans:[{spans:[{spanId:'child',parentSpanId:'parent',name:'consume',startTimeUnixNano:'1000000',endTimeUnixNano:'4500000'}]}]}]});
  assert.equal(result[0].duration,3.5);assert.equal(result[0].service,'telemetry');assert.equal(result[0].parentId,'parent');
  assert.deepEqual(traceSpans({}),[]);
});
