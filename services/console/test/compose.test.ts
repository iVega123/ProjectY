import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {test} from 'node:test';
import {COMPOSITION_CALLS, MAX_BATCH, PAGE_SIZE, compose, distinct} from '../lib/compose.ts';
import type {Rental} from '../lib/types.ts';

const declaration = JSON.parse(readFileSync(new URL('../../../contracts/reads.json', import.meta.url), 'utf8'));

function rentals(count:number):Rental[] {
  return Array.from({length:count},(_,index)=>({
    rentalId:'r'+index, userId:'rider-1', motorcycleId:'m'+index,
    motorcycleLicencePlate:'ABC'+String(index).padStart(4,'0'),
    startDate:'2026-09-01T00:00:00Z', predictedEndDate:'2026-09-08T00:00:00Z',
    actualEndDate:null, originalTotalCost:210,
  }));
}

/** Um duplo que grava cada caminho pedido e responde o que a rota responderia. */
function recorder(reply:(path:string)=>unknown = () => []) {
  const paths:string[] = [];
  const call = async (path:string) => {
    paths.push(path);
    return new Response(JSON.stringify(reply(path)),{headers:{'Content-Type':'application/json'}});
  };
  return {paths,call};
}

/**
 * A medida que o #138 pede: "a bounded number of downstream calls, independent
 * of how many rows are on it — measured, not assumed". Uma linha e uma página
 * cheia custam o mesmo, e o teste é o que impede alguém de escrever um `await`
 * dentro do `map` sem perceber.
 */
test('composing a page costs the same number of calls at one row and at a full page',async()=>{
  const single = recorder();
  await compose(rentals(1),'rider-1',single.call);
  const full = recorder();
  await compose(rentals(PAGE_SIZE),'rider-1',full.call);

  assert.equal(single.paths.length,COMPOSITION_CALLS);
  assert.equal(full.paths.length,COMPOSITION_CALLS);
  // E são um lote por provedor, não um pedido por linha.
  assert.equal(full.paths.filter(path=>path.startsWith('/api/motorcycles/batch')).length,1);
  assert.equal(full.paths.filter(path=>path.startsWith('/api/invoices')).length,1);
  assert.equal(full.paths.filter(path=>path.startsWith('/api/riders/')).length,1);
});

/**
 * O tamanho de página e o teto do lote são o mesmo número por acordo entre o
 * console e os provedores. Subir um sem o outro deixaria a última linha da
 * página sem moto, em silêncio -- então fica vermelho aqui primeiro.
 */
test('the page size and every declared batch cap are the same number',()=>{
  assert.equal(PAGE_SIZE,MAX_BATCH);
  assert.equal(declaration.pageSize,PAGE_SIZE);
  for (const read of declaration.reads.filter((read:{batched:boolean})=>read.batched)) {
    assert.equal(read.maxBatch,MAX_BATCH,read.name+' declares a different cap');
  }
});

/** O que a tela pede é o que a declaração diz, e nada além. */
test('the composed screen only calls what contracts/reads.json declares',async()=>{
  const recorded = recorder();
  await compose(rentals(3),'rider-1',recorded.call);

  const declared = declaration.reads.map((read:{path:string})=>read.path);
  for (const path of recorded.paths) {
    const matched = declared.some((template:string)=>
      path.startsWith(template.replace('/{id}','/')) || path.startsWith(template+'?'));
    assert.ok(matched,path+' is not declared in contracts/reads.json');
  }
  assert.equal(recorded.paths.length,declaration.reads.length);
});

/**
 * A postura de falha do ADR 0014: a agregação falha SUAVE. Uma nota que não
 * chegou deixa a linha sem valor final e a tela diz o que faltou -- ela não
 * apaga a página, que é o que o portão faria com a requisição inteira.
 */
test('a provider that is down leaves the row without that field, not without the page',async()=>{
  const call = async (path:string) => path.startsWith('/api/invoices')
    ? new Response('upstream unavailable',{status:503})
    : new Response(JSON.stringify(path.startsWith('/api/riders/')?{userId:'rider-1',name:'Ada'}:[]),
      {headers:{'Content-Type':'application/json'}});

  const composed = await compose(rentals(2),'rider-1',call);

  assert.equal(composed.items.length,2);
  assert.equal(composed.items[0].invoice,null);
  assert.deepEqual(composed.missing,['invoices']);
  assert.equal(composed.rider?.name,'Ada');
});

test('a provider that throws is the same as one that refuses',async()=>{
  const composed = await compose(rentals(1),'rider-1',async()=>{throw new Error('connect ECONNREFUSED')});
  assert.equal(composed.items.length,1);
  assert.deepEqual(composed.missing.sort(),['invoices','motorcycles','rider']);
});

/**
 * 404 é resposta, não falha. Um administrador não tem registro de piloto, e a
 * tela dizer "sem piloto" está certo -- dizer "o identity caiu" seria mentira,
 * e mentira que aparece toda vez que um administrador entra.
 *
 * O fixture de carga achou isto: o sujeito do token dele não é um piloto
 * cadastrado, e a tela anunciava degradação a cada requisição.
 */
test('a record that does not exist is an answer, not a degradation',async()=>{
  const call = async (path:string) => path.startsWith('/api/riders/')
    ? new Response('{"detail":"piloto nao encontrado"}',{status:404})
    : new Response('[]',{headers:{'Content-Type':'application/json'}});

  const composed = await compose(rentals(1),'an-admin',call);

  assert.equal(composed.rider,null);
  assert.deepEqual(composed.missing,[]);
});

/** Uma página vazia não gasta lote nenhum: um lote sem ids é 400 do outro lado. */
test('an empty page asks for no batches',async()=>{
  const recorded = recorder();
  await compose([],'rider-1',recorded.call);
  assert.deepEqual(recorded.paths,['/api/riders/rider-1']);
});

test('repeated ids are asked for once',async()=>{
  assert.deepEqual(distinct(['a','a','b',null,undefined,'']),['a','b']);
  const shared = rentals(3).map(rental=>({...rental,motorcycleId:'same-bike'}));
  const recorded = recorder();
  await compose(shared,'rider-1',recorded.call);
  const batch = recorded.paths.find(path=>path.startsWith('/api/motorcycles/batch'))!;
  assert.equal(batch,'/api/motorcycles/batch?ids=same-bike');
});
