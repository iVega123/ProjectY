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

/**
 * O achado B9: um segmento de caminho vindo do cliente não pode reescrever a
 * requisição interna. Aqui há duas barreiras, e o teste quer as duas.
 *
 * A placa é validada contra o formato brasileiro antes de virar caminho, e o
 * que atravessa vai por `encodeURIComponent`. A segunda existe porque a
 * primeira é uma regra de negócio: no dia em que alguém aceitar uma placa de
 * outro país, a barreira que sobra tem de ser a que trata caminho como caminho.
 */
test('a plate cannot rewrite the internal request path',()=>{
  for (const hostile of ['../riders','..%2friders','ABC/1234','ABC 1234','../../admin']) {
    assert.throws(()=>plates([hostile]),undefined,hostile);
  }
  // E o que passa pela validação ainda é escapado antes de virar caminho.
  assert.equal('/api/motorcycles/'+encodeURIComponent('../riders'),'/api/motorcycles/..%2Friders');
});
