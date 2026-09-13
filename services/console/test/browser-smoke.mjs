import {chromium} from '@playwright/test';
import http from 'node:http';
import net from 'node:net';
import {mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import assert from 'node:assert/strict';

// Local forwarding keeps browser origins identical to the published fixture URL.
function forward(port,host,target) {
  const server=http.createServer((request,response)=>{
    const upstream=http.request({host,port:target,path:request.url,method:request.method,headers:request.headers},r=>{response.writeHead(r.statusCode,r.headers);r.pipe(response)});
    upstream.on('error',()=>{response.writeHead(502);response.end()});request.pipe(upstream);
  });
  server.on('upgrade',(request,socket,head)=>{
    const upstream=net.connect(target,host,()=>{
      upstream.write(`${request.method} ${request.url} HTTP/${request.httpVersion}\r\n`+request.rawHeaders.reduce((s,h,i)=>s+h+(i%2?'\r\n':': '),'')+'\r\n');
      if(head.length) upstream.write(head);socket.pipe(upstream);upstream.pipe(socket);
    });
    upstream.on('error',()=>socket.destroy());socket.on('error',()=>upstream.destroy());
  });
  server.listen(port,'127.0.0.1');return server;
}
const proxies=[forward(13001,'console',3001),forward(14000,'telemetry',4000)];
const browser=await chromium.launch({headless:true,args:['--no-sandbox']});
const page=await browser.newPage({viewport:{width:1440,height:1120},deviceScaleFactor:1,
  geolocation:{latitude:-3.119,longitude:-60.021},permissions:['geolocation']});
const failures=[];
async function selectRental(id) {
  const picker=page.getByLabel('Active rental');
  for(let n=0;n<100;n++) {
    if(await picker.locator(`option[value="${id}"]`).count()) {await picker.selectOption(id);return}
    const pending=page.waitForResponse(r=>r.url().includes('/api/session?cursor='));
    await page.getByRole('button',{name:'Next rental page →',exact:true}).click();
    const result=await pending;
    const data=await result.json();
    if(result.status()===400 && data.error?.includes('rate limit')) {
      await page.waitForTimeout(2000);
      continue;
    }
    assert.equal(result.status(),200);
    await picker.locator(`option[value="${data.rentals.items[0].rentalId}"]`).waitFor({state:'attached'});
  }
  throw new Error('Fixture rental not found in paginated console');
}
page.on('pageerror',e=>failures.push(e.message));
mkdirSync('docs/images',{recursive:true});
try {
  const {token}=await (await fetch('http://load-identity:8080/token')).json();
  await page.goto('http://localhost:13001');
  await page.getByLabel('Gateway access token').fill(token);
  await page.getByRole('button',{name:'Connect ↗',exact:true}).click();
  await page.getByText('Session connected',{exact:true}).waitFor();
  const fixture=JSON.parse(readFileSync('.env.console-session.json','utf8'));
  await selectRental(fixture.rentalId);
  await page.getByText('Connected',{exact:true}).waitFor({timeout:30000});
  await page.getByText('-3.11900, -60.02100',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Send my location ↗',exact:true}).click();
  await page.getByText('0s since last update',{exact:true}).waitFor();
  await page.screenshot({path:'docs/images/console-live-map.png',fullPage:true});

  const plate='KAA6'+String(Date.now()%1000).padStart(3,'0');
  await page.getByLabel('Licence plate',{exact:true}).fill(plate);
  await page.getByRole('button',{name:'Start rental ↗',exact:true}).click();
  await page.locator('.span-row').first().waitFor({timeout:60000});
  await page.locator('.span-row').filter({hasText:'telemetry'}).first().waitFor({timeout:60000});
  await page.screenshot({path:'docs/images/console-system-xray.png',fullPage:true});

  await page.getByRole('button',{name:'Load generator'}).click();
  for(let batch=0;batch<2;batch++) {
    await page.getByLabel('Motorcycle plates').fill(Array.from({length:100},(_,i)=>'KAA'+String(4400+batch*100+i).padStart(4,'0')).join(' '));
    await page.getByRole('button',{name:'Run batch ↗',exact:true}).click();
    await page.getByRole('button',{name:'Run batch ↗',exact:true}).waitFor({timeout:30000});
  }
  await page.locator('.result').filter({hasText:'429'}).first().waitFor({timeout:15000});
  await page.waitForFunction(()=>!document.querySelector('.metric-value')?.textContent?.includes('—'),{},{timeout:90000});
  await page.screenshot({path:'docs/images/console-load-generator.png',fullPage:true});

  await page.getByRole('button',{name:'Live map'}).click();
  await selectRental(fixture.rentalId);
  await page.getByText('-3.11900, -60.02100',{exact:true}).waitFor();
  await page.getByText('Connected',{exact:true}).waitFor({timeout:60000});
  writeFileSync('.env.console-freeze-ready','ready');
  await page.getByText('Frozen · reconnecting',{exact:true}).waitFor({timeout:60000});
  assert.equal(await page.getByText('-3.11900, -60.02100',{exact:true}).count(),1);
  await page.screenshot({path:'docs/images/console-tracking-frozen.png',fullPage:true});
  assert.deepEqual(failures,[]);
  writeFileSync('docs/measurements/polyglot-browser.json',JSON.stringify({measuredAt:new Date().toISOString(),browser:'Chromium / Playwright 1.63.0',viewport:{width:1440,height:1120},passed:['authenticated map and cached position','real rental trace with Kafka consumer','200 batch attempts with observed 429','stopped telemetry freezes last marker'],pageErrors:failures},null,2)+'\n');
  console.log('PASS: map, trace, load and stop/freeze; screenshots captured');
} catch(error) {
  await page.screenshot({path:'docs/images/console-test-failure.png',fullPage:true});
  console.error((await page.locator('body').innerText()).slice(0,5000));
  throw error;
} finally {await browser.close();proxies.forEach(p=>p.close())}
