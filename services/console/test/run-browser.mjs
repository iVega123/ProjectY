import {spawn,execFileSync} from 'node:child_process';
import {existsSync,unlinkSync} from 'node:fs';
const compose=['compose','-p','projecty-load','-f','.env.load-compose.json'];
const marker='.env.console-freeze-ready';
if(existsSync(marker)) unlinkSync(marker);
execFileSync('docker',[...compose,'start','telemetry'],{stdio:'inherit'});
const child=spawn('docker',['run','--rm','--name','projecty-console-browser','--network','projecty-load_projecty',
  '-v',process.cwd()+':/workspace','-w','/workspace','--ipc=host','mcr.microsoft.com/playwright:v1.63.0-noble',
  'node','services/console/test/browser-smoke.mjs'],{stdio:'inherit'});
let stopped=false;
const timer=setInterval(()=>{
  if(!stopped && existsSync(marker)) {
    stopped=true;
    execFileSync('docker',[...compose,'stop','telemetry'],{stdio:'inherit'});
  }
},250);
child.on('exit',code=>{
  clearInterval(timer);
  try {execFileSync('docker',[...compose,'start','telemetry'],{stdio:'inherit'})}
  finally {if(existsSync(marker)) unlinkSync(marker);process.exitCode=code ?? 1}
});
