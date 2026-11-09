// Offline child-only fixture. Replaces node:https before importing production.
// Every request is synthetic; no server, socket, real token, or live log exists.
import https from 'node:https';
import {EventEmitter} from 'node:events';
import {Readable} from 'node:stream';
import {appendFileSync} from 'node:fs';
import {syncBuiltinESMExports} from 'node:module';
const f=JSON.parse(process.env.WSS_JOB_LOG_FIXTURE),timer=globalThis.setTimeout;
globalThis.setTimeout=(fn,ms,...args)=>{
  if(ms===120000)appendFileSync(process.env.WSS_JOB_LOG_TRACE,JSON.stringify({event:'total-start'})+'\n');
  return timer(()=>{if(ms===120000)appendFileSync(process.env.WSS_JOB_LOG_TRACE,JSON.stringify({event:'total-fired'})+'\n');fn(...args);},f.fastDeadline&&ms===120000?30:f.fastHeaders&&ms===15000?10:ms);
};
let calls=0;
https.request=(url,options,callback)=>{
  const index=calls++,spec=f.responses?.[index]||{},req=new EventEmitter();
  appendFileSync(process.env.WSS_JOB_LOG_TRACE,JSON.stringify({url:String(url),method:options.method,headers:options.headers,rejectUnauthorized:options.rejectUnauthorized})+'\n');
  let response;
  req.destroy=error=>{req.emit('error',error);response?.destroy(error);};
  req.setTimeout=(ms,callback)=>{if(ms!==30000)throw new Error('Expected bounded socket inactivity');if(spec.stall)timer(callback,20);return req;};
  options.signal.addEventListener('abort',()=>{req.emit('error',new Error('synthetic secret timeout'));response?.destroy(new Error('synthetic body timeout'));},{once:true});
  req.end=()=>queueMicrotask(()=>{
    if(spec.error){req.emit('error',new Error('fake-token '+spec.error));return;}
    if(spec.hang)return;
    async function* body(){
      if(spec.trickle){while(!options.signal.aborted){appendFileSync(process.env.WSS_JOB_LOG_TRACE,JSON.stringify({event:'chunk'})+'\n');yield Buffer.from('x');await new Promise(resolve=>timer(resolve,2));}return;}
      if(spec.partial){yield Buffer.from('partial\x1b[31m');throw new Error('signed-location synthetic body');}
      if(spec.bytes)yield Buffer.from(spec.bytes,'base64');
      if(spec.chunkSize)for(let i=0;i<(spec.chunks||1);i++)yield Buffer.alloc(spec.chunkSize,65);
    }
    response=spec.stall?new Readable({read(){}}):Readable.from(body(),{objectMode:false});response.statusCode=spec.status??200;
    response.headers=spec.headers||{};response.complete=spec.complete!==false;response.aborted=spec.aborted===true;
    callback(response);
  });
  return req;
};
syncBuiltinESMExports();
