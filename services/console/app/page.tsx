'use client';
import { useEffect, useRef, useState } from 'react';
import dynamic from 'next/dynamic';
import type { Attempt, Composed, Metrics, Position, RiderCard, Span } from '../lib/types';
const LiveMap = dynamic(() => import('./map'), {ssr:false});
const money = (n:number) => new Intl.NumberFormat('en-US',{style:'currency',currency:'BRL'}).format(n);
// A nota vem em centavos, como o contrato do evento. Dividir na borda é o
// último lugar onde isso acontece, e o único que mostra o número a alguém.
const minor = (n:number, currency:string) => new Intl.NumberFormat('en-US',{style:'currency',currency}).format(n/100);
async function json(url:string, init?:RequestInit) {
  const response = await fetch(url,init); const body = await response.json();
  if (!response.ok) throw new Error(body.error ?? 'Request failed');
  return body;
}
export default function Console() {
  const [user,setUser] = useState('');
  const [rentals,setRentals] = useState<Composed[]>([]);
  const [rider,setRider] = useState<RiderCard|null>(null);
  // O que a composição não conseguiu trazer. A tela renderiza o que chegou e
  // diz o que faltou, em vez de fingir que a página está completa -- ADR 0014.
  const [missing,setMissing] = useState<string[]>([]);
  const [selected,setSelected] = useState('');
  const [cursor,setCursor] = useState('');
  const [nextCursor,setNextCursor] = useState<string|null>(null);
  const [position,setPosition] = useState<Position|null>(null);
  const [connection,setConnection] = useState('Disconnected');
  const [presence,setPresence] = useState(0);
  const [error,setError] = useState('');
  const [tab,setTab] = useState('Live map');
  const [attempts,setAttempts] = useState<Attempt[]>([]);
  const [trace,setTrace] = useState<Attempt|null>(null);
  const [spans,setSpans] = useState<Span[]>([]);
  const [traceStatus,setTraceStatus] = useState('Start a rental to inspect its journey.');
  const [metrics,setMetrics] = useState<Metrics|null>(null);
  const [busy,setBusy] = useState(false);
  const [plate,setPlate] = useState('');
  const [batch,setBatch] = useState('');
  const [start,setStart] = useState('');
  const [days,setDays] = useState(7);
  const [now,setNow] = useState(0);
  const socket = useRef<WebSocket|null>(null);
  const requestId = useRef(10);
  async function refresh(pageCursor = '') {
    const data = await json('/api/session'+(pageCursor?'?cursor='+encodeURIComponent(pageCursor):''));
    setCursor(pageCursor); setNextCursor(data.rentals.nextCursor);
    setUser(data.userId); setRentals(data.rentals.items);
    setRider(data.rider ?? null); setMissing(data.rentals.missing ?? []);
    setSelected(current => data.rentals.items.some((r:Composed)=>r.rentalId===current) ? current : data.rentals.items.find((r:Composed) => !r.actualEndDate)?.rentalId || '');
  }
  useEffect(() => {
    setNow(Date.now());
    setStart(new Date(Date.now()+86400000).toISOString().slice(0,10));
    refresh().catch(() => {});
    const timer = setInterval(() => setNow(Date.now()),1000);
    return () => clearInterval(timer);
  },[]);
  useEffect(() => {
    if (!user) return;
    let active = true;
    const read = () => json('/api/metrics').then(data => {if(active) setMetrics(data)}).catch(() => {if(active) setMetrics(null)});
    read(); const timer = setInterval(read,10000);
    return () => {active=false; clearInterval(timer)};
  },[user]);
  useEffect(() => {
    setPosition(null); setPresence(0);
    if (!selected || !user) return;
    let cancelled=false; let retry:ReturnType<typeof setTimeout>; let heartbeat:ReturnType<typeof setInterval>;
    async function connect() {
      try {
        setConnection('Connecting');
        const auth = await json('/api/tracking?rentalId='+encodeURIComponent(selected)+'&cursor='+encodeURIComponent(cursor));
        if(cancelled) return;
        const ws = new WebSocket(auth.socketUrl+'?vsn=2.0.0&ticket='+encodeURIComponent(auth.ticket));
        socket.current=ws;
        ws.onopen = () => ws.send(JSON.stringify(['1','1','rental:'+selected,'phx_join',{}]));
        ws.onmessage = ({data}) => {
          const [,ref,,event,body] = JSON.parse(data);
          if(event==='phx_reply' && ref==='1') {
            if(body.status!=='ok') {setError(body.response?.reason ?? 'Tracking unavailable'); ws.close(); return}
            setConnection('Connected');
            if(body.response?.position) setPosition(body.response.position);
          }
          if(event==='position') setPosition(body);
          if(event==='phx_close' || event==='phx_error') ws.close();
          if(event==='presence_state') setPresence(Object.values(body).reduce<number>((count,value) => count+((value as {metas:unknown[]}).metas?.length ?? 0),0));
          if(event==='presence_diff') setPresence(count => Math.max(0,count+Object.keys(body.joins??{}).length-Object.keys(body.leaves??{}).length));
          if(event==='phx_reply' && ref!=='1' && body.status==='error') setError(body.response?.reason ?? 'Position rejected');
        };
        ws.onclose = () => {clearInterval(heartbeat); if(!cancelled) {setConnection('Frozen · reconnecting'); retry=setTimeout(connect,5000)}};
        ws.onerror = () => ws.close();
        heartbeat=setInterval(() => {if(ws.readyState===WebSocket.OPEN) ws.send(JSON.stringify([null,String(++requestId.current),'phoenix','heartbeat',{}]))},25000);
      } catch(e) {if(!cancelled) {setConnection('Frozen · unavailable'); setError(String(e)); retry=setTimeout(connect,10000)}}
    }
    connect();
    return () => {cancelled=true; clearTimeout(retry); clearInterval(heartbeat); socket.current?.close()};
  },[selected,user,cursor]);
  useEffect(() => {
    if(!trace) return;
    let active=true; let rounds=0;
    setSpans([]); setTraceStatus('Waiting for collector export…');
    const read = async () => {
      try {
        const data = await json('/api/trace?id='+trace.traceId+'&grant='+encodeURIComponent(trace.grant));
        if(!active) return;
        setSpans(data.spans); setTraceStatus(data.spans.length ? 'Observed spans · polling for asynchronous consumers' : 'Trace has not reached storage yet');
      } catch(e) {if(active) setTraceStatus(String(e))}
      rounds++;
    };
    read(); const timer=setInterval(() => {if(rounds<18) read(); else {clearInterval(timer); if(active) setTraceStatus('Capture complete · select the action again to refresh')}},10000);
    return () => {active=false;clearInterval(timer)};
  },[trace]);
  async function login(event:React.FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError('');
    const form=new FormData(event.currentTarget);
    try {await json('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(Object.fromEntries(form))}); await refresh()}
    catch(e) {setError(String(e))}
  }
  async function run(selectedPlates:string[]) {
    setBusy(true);setAttempts([]);setError('');
    try {
      const date=new Date(start+'T00:00:00Z');
      const response=await fetch('/api/rentals',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({plates:selectedPlates,startDate:date.toISOString(),predictedEndDate:new Date(date.getTime()+days*86400000).toISOString()})});
      if(!response.ok) throw new Error((await response.json()).error);
      const reader=response.body!.getReader(); const decoder=new TextDecoder(); let pending='';
      while(true) {
        const {done,value}=await reader.read(); if(done) break;
        pending+=decoder.decode(value,{stream:true}); const lines=pending.split('\n'); pending=lines.pop()!;
        for(const line of lines) {if(!line) continue; const result:Attempt=JSON.parse(line); setAttempts(rows=>[...rows,result]); if(selectedPlates.length===1) {setTrace(result);setTab('System x-ray')}}
      }
      await refresh();
    } catch(e) {setError(String(e))} finally {setBusy(false)}
  }
  function locate() {
    if(!navigator.geolocation) {setError('Geolocation unavailable');return}
    navigator.geolocation.getCurrentPosition(({coords}) => {
      if(socket.current?.readyState!==WebSocket.OPEN) {setError('Tracking is disconnected');return}
      socket.current.send(JSON.stringify(['1',String(++requestId.current),'rental:'+selected,'position',{latitude:coords.latitude,longitude:coords.longitude}]));
    },e=>setError(e.message),{enableHighAccuracy:true,timeout:10000});
  }
  const ok=attempts.filter(a=>a.status>=200&&a.status<300).length;
  const limited=attempts.filter(a=>a.status===429).length;
  const age=position ? Math.max(0,Math.floor((now-position.recorded_at)/1000)) : null;
  const traceStart=spans.length ? Math.min(...spans.map(s=>s.start)) : 0;
  const traceLength=spans.length ? Math.max(...spans.map(s=>s.start+s.duration))-traceStart : 1;
  return <div className="shell">
    <aside className="rail"><a className="brand" href="/" aria-label="ProjectY home">Y<span>↗</span></a><div className="rail-word">OPERATIONS</div><span className="rail-foot">PY / 10</span></aside>
    <main><header><div className="breadcrumb">PROJECT Y <span>/</span> OPERATIONS CONSOLE</div><div className="header-state"><i className={user?'dot':'dot muted'}/>{user?(rider?rider.name+' · CNH '+rider.cnhType:'Session connected'):'Sign-in required'}</div></header>
      <section className="intro"><div><div className="eyebrow">THE SYSTEM, IN MOTION</div><h1>Watch every move<span>.</span></h1><p>From a rider on the street to the events behind the ride.</p></div><div className="clock">{new Date(now).toISOString().slice(11,19)}<small>UTC / LIVE WORKSPACE</small></div></section>
      {!user && <form className="login panel" onSubmit={login}><div><strong>Connect to your fleet</strong><p>Use an access token from your configured identity provider.</p></div><label className="token-input">Gateway access token<input name="token" type="password" required autoComplete="off" maxLength={8192}/></label><button>Connect ↗</button></form>}
      {error && <div className="error" role="alert">{error}<button className="text" onClick={()=>setError('')}>Dismiss ×</button></div>}
      <section className="metrics"><Metric label="RENTAL LATENCY / P99" value={metrics?.p99==null?'—':metrics.p99.toFixed(0)} unit="ms" note="Rolling 5 minutes"/><Metric label="READY IN QUEUE" value={metrics?.queue==null?'—':String(metrics.queue)} unit="messages" note="RabbitMQ · current depth"/><Metric label="RATE LIMITED" value={metrics?.limited==null?'—':metrics.limited.toFixed(0)} unit="requests" note="Gateway · last 5 minutes"/><Metric label="THIS BATCH" value={String(ok)} unit={'/ '+attempts.length} note={limited+' rejected by rate limit'}/></section>
      <nav className="tabs">{['Live map','System x-ray','Load generator'].map((name,i)=><button key={name} onClick={()=>setTab(name)} className={tab===name?'active':''}><span>0{i+1}</span>{name}</button>)}<span className="tabs-note">{metrics?'Updated '+new Date(metrics.measuredAt).toISOString().slice(11,19)+' UTC':'Waiting for stack metrics'}</span></nav>
      {tab==='Live map' && <section className="workspace"><div className="map-panel panel"><LiveMap position={position}/><div className="map-label"><i className={connection==='Connected'?'dot':'dot muted'}/>{connection}</div>{!position && <div className="map-empty">{selected?'Waiting for the rider’s first position':'Select an active rental to follow its position'}</div>}<div className="map-bottom"><strong>{position?position.latitude.toFixed(5)+', '+position.longitude.toFixed(5):'No position received'}</strong><span>{age===null?'Positions arrive over a live connection':age+'s since last update'}</span></div></div><aside className="detail panel"><div className="eyebrow">LIVE RENTAL</div><h2>Follow the ride.</h2><p>Select a rental from your account. The marker moves only when a real position arrives.</p><label>Active rental<select value={selected} onChange={e=>setSelected(e.target.value)}><option value="">Choose a rental</option>{rentals.filter(r=>!r.actualEndDate).map(r=><option key={r.rentalId} value={r.rentalId}>{r.motorcycleLicencePlate} · {r.rentalId.slice(-6)}</option>)}</select></label><div className="detail-row"><span>Presence</span><strong>{presence} connected</strong></div><div className="detail-row"><span>Last position</span><strong>{age===null?'Awaiting data':age+'s ago'}</strong></div><button disabled={!selected||connection!=='Connected'} onClick={locate}>Send my location ↗</button><p className="caption">Uses your device’s location with your permission. When tracking disconnects, the last marker stays in place.</p><button className="secondary" disabled={!user} onClick={()=>refresh().catch(e=>setError(String(e)))}>Refresh rentals ↻</button></aside></section>}
      {tab==='System x-ray' && <section className="panel trace-panel"><div className="section-heading"><div><div className="eyebrow">DISTRIBUTED TRACE</div><h2>One action. Every hop.</h2></div><span className="tag">{spans.length} OBSERVED SPANS</span></div><p className="caption">{traceStatus}</p>{trace && <code className="trace-id">{trace.traceId}</code>}{spans.length>0 ? <div className="waterfall">{spans.map((span,i)=><div className="span-row" key={span.id+i}><div><strong>{span.service}</strong><small title={span.name}>{span.name}</small></div><div className="span-track"><div className={'span-bar '+(span.error?'bad':'')} style={{marginLeft:((span.start-traceStart)/Math.max(traceLength,1)*80)+'%',width:Math.max(0.5,span.duration/Math.max(traceLength,1)*80)+'%'}}/><span>{span.duration.toFixed(1)} ms</span></div></div>)}</div>:<div className="empty-state"><span>⌁</span><h3>{trace?'Waiting for trace data':'See the architecture happen.'}</h3><p>Start a rental below. Its actual service calls and asynchronous work will appear here.</p></div>}</section>}
      {tab==='Load generator' && <section className="panel load-panel"><div className="section-heading"><div><div className="eyebrow">CONTROLLED LOAD</div><h2>Put the flow to work.</h2></div><span className="tag">MAX 100 / BATCH</span></div><p>Enter existing motorcycle plates, separated by spaces or commas. Each plate triggers one real rental attempt under your account.</p><label>Motorcycle plates<textarea rows={3} value={batch} onChange={e=>setBatch(e.target.value.toUpperCase())} placeholder="Enter plates from your test fleet"/></label><div className="load-actions"><span>{batch.trim()?batch.trim().split(/[\s,]+/).length:0} concurrent attempts · uses the dates below</span><button disabled={!user||busy||!batch.trim()} onClick={()=>run(batch.trim().split(/[\s,]+/))}>{busy?'Running…':'Run batch ↗'}</button></div><Results attempts={attempts} select={a=>{setTrace({...a});setTab('System x-ray')}}/></section>}
      <section className="action-bar panel"><div><div className="eyebrow">CREATE A RENTAL</div><strong>Set a journey in motion</strong></div><label>Licence plate<input value={plate} onChange={e=>setPlate(e.target.value.toUpperCase())} placeholder="Your motorcycle plate" maxLength={7}/></label><label>Start date<input type="date" value={start} onChange={e=>setStart(e.target.value)}/></label><label>Plan<select value={days} onChange={e=>setDays(Number(e.target.value))}>{[7,15,30,45].map(n=><option key={n} value={n}>{n} days</option>)}</select></label><button disabled={!user||busy||!plate} onClick={()=>run([plate])}>{busy?'Sending…':'Start rental ↗'}</button></section>
      {tab!=='Load generator' && attempts.length>0 && <section className="panel"><Results attempts={attempts} select={a=>{setTrace({...a});setTab('System x-ray')}}/></section>}
      {rentals.length>0 && <section className="rental-list"><div className="section-heading"><h2>Your recent rentals</h2><span className="caption">{rentals.length} in the current page{missing.length?' · without '+missing.join(' and '):''}</span></div>{rentals.slice(0,5).map(r=><div className="rental-row" key={r.rentalId}><strong>{r.motorcycleLicencePlate}<small>{r.motorcycle?r.motorcycle.model+' · '+r.motorcycle.year:'model unavailable'}</small></strong><span>{r.startDate.slice(0,10)} → {r.predictedEndDate.slice(0,10)}</span><span>{money(r.originalTotalCost)}<small>agreed</small></span><span>{r.invoice?minor(r.invoice.totalMinor,r.invoice.currency):'—'}<small>{r.invoice?r.invoice.reason:r.actualEndDate?'invoice pending':'not settled yet'}</small></span><span className="tag">{r.actualEndDate?'CLOSED':'ACTIVE'}</span></div>)}</section>}
      {user && <div className="load-actions"><button className="secondary" disabled={!cursor} onClick={()=>refresh().catch(e=>setError(String(e)))}>First rental page</button><button className="secondary" disabled={!nextCursor} onClick={()=>refresh(nextCursor!).catch(e=>setError(String(e)))}>Next rental page →</button></div>}
      <footer><span>PROJECT Y <b>POLYGLOT OPERATIONS</b></span><span>Live data · UTC timestamps</span>{user&&<button className="text" onClick={async()=>{await fetch('/api/session',{method:'DELETE'});setUser('');setRentals([]);setSelected('');setMetrics(null);setRider(null);setMissing([])}}>Sign out ↗</button>}</footer>
    </main></div>;
}
function Metric({label,value,unit,note}:{label:string;value:string;unit:string;note:string}) {return <div className="metric"><div className="eyebrow">{label}</div><div className="metric-value">{value}<span>{unit}</span></div><small>{note}</small></div>}
function Results({attempts,select}:{attempts:Attempt[];select:(a:Attempt)=>void}) {return <div className="results">{attempts.length>0&&<div className="results-head">ATTEMPT RESULTS <span>Select an action to inspect its trace</span></div>}{attempts.map(a=><button className="result" key={a.index} onClick={()=>select(a)}><strong>{a.plate}</strong><span className={a.status<300?'success':'failure'}>{a.status}</span><span>{a.duration.toFixed(0)} ms</span><span className="result-detail">{a.detail}</span><span>View trace ↗</span></button>)}</div>}
