import test from 'node:test';
import assert from 'node:assert/strict';
import childProcess from 'node:child_process';
import fs from 'node:fs';
import {syncBuiltinESMExports} from 'node:module';
import {createHash} from 'node:crypto';
import {tmpdir} from 'node:os';
import {basename,dirname,isAbsolute,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {jobLogBytes} from './preflight-proof.mjs';
const repository='cwru-non-academic/CLI_CSharp_WSS_Application',job='123',token='fake-token';
const helper=fileURLToPath(new URL('./job-log.mjs',import.meta.url));
const preload=new URL('./fixtures/job-log-https.mjs',import.meta.url).href;
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');

function fixture(spec,action,{intercept,prepareFile,inspectPrivate=false,fastParent=false,afterChild,residual}={}){
  const originalExec=childProcess.execFileSync,originalTemp=fs.mkdtempSync,originalOpen=fs.openSync;
  const root=originalTemp(join(tmpdir(),'wss-job-log-test-')),trace=join(root,'requests.jsonl'),directories=[],outputs=[],errors=[];
  let launched=0,preparing=false;
  fs.mkdtempSync=(...args)=>{const path=originalTemp(...args);directories.push(path);return path;};
  fs.openSync=(path,...args)=>{if(prepareFile&&!preparing&&String(path).endsWith('body')){preparing=true;prepareFile(path);}return originalOpen(path,...args);};
  childProcess.execFileSync=(command,args,options)=>{
    if(command!==process.execPath||args[0]!==helper)return originalExec(command,args,options);
    launched++;assert.equal(options.stdio[0],'pipe');assert.equal(options.stdio[1],'pipe');assert.equal(options.stdio[2],'pipe');assert.equal(typeof options.stdio[3],'number');
    assert.equal(options.maxBuffer,1024);assert.equal(options.timeout,125000);assert.equal(options.killSignal,'SIGKILL');
    assert.equal(options.env.GH_TOKEN,undefined);assert.equal(options.env.NODE_OPTIONS,undefined);assert.equal(options.env.NODE_TLS_REJECT_UNAUTHORIZED,undefined);
    assert.equal(JSON.parse(options.input).token,token);assert.ok(!args.some(arg=>arg.includes(token)));
    if(inspectPrivate){
      const directory=directories.at(-1),file=join(directory,'body');
      assert.equal(fs.lstatSync(directory).isSymbolicLink(),false);assert.equal(fs.lstatSync(file).isSymbolicLink(),false);
      if(process.platform==='win32'){
        const script="$ErrorActionPreference='Stop';$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$dir=Get-Acl -LiteralPath $env:WSS_PRIVATE_LOG_DIRECTORY;if(!$dir.AreAccessRulesProtected){throw 'Directory ACL must be protected'};foreach($path in @($env:WSS_PRIVATE_LOG_DIRECTORY,(Join-Path $env:WSS_PRIVATE_LOG_DIRECTORY 'body'))){$acl=Get-Acl -LiteralPath $path;$rules=$acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]);if($rules.Count -ne 1 -or $rules[0].IdentityReference -ne $sid -or $rules[0].AccessControlType -ne 'Allow'){throw 'Private current-user ACL required'}}";
        originalExec('pwsh',['-NoProfile','-NonInteractive','-EncodedCommand',Buffer.from(script,'utf16le').toString('base64')],{env:{...options.env,WSS_PRIVATE_LOG_DIRECTORY:directory},stdio:['ignore','pipe','pipe'],maxBuffer:1024,timeout:10000,windowsHide:true});
      }else{
        assert.equal(fs.statSync(directory).mode&0o077,0);assert.equal(fs.statSync(file).mode&0o077,0);
      }
    }
    if(intercept)return intercept(command,args,options);
    let output;
    try{output=originalExec(command,['--import',preload,...args],{...options,timeout:fastParent?200:options.timeout,env:{...options.env,WSS_JOB_LOG_FIXTURE:JSON.stringify(spec),WSS_JOB_LOG_TRACE:trace}});}
    catch(error){errors.push({code:error.code,signal:error.signal});throw error;}
    afterChild?.(directories.at(-1),options.stdio[3]);
    outputs.push(output);assert.match(output,/^\{"bytes":\d+\}\n$/);return output;
  };
  syncBuiltinESMExports();
  try{
    const result=action({environment:{GH_TOKEN:token},command(){throw new Error('gh must never be called');}});
    const events=fs.existsSync(trace)?fs.readFileSync(trace,'utf8').trim().split('\n').map(JSON.parse):[],requests=events.filter(e=>e.method);
    return {result,requests,events,launched,outputs,errors};
  }finally{
    childProcess.execFileSync=originalExec;fs.mkdtempSync=originalTemp;fs.openSync=originalOpen;syncBuiltinESMExports();
    for(const directory of directories){residual?.(directory);assert.equal(fs.existsSync(directory),false,'Private transport directory must be cleaned');}
    removeFixture(root);
  }
}
function removeFixture(root){
  const path=fs.realpathSync(root);assert.ok(isAbsolute(path));assert.equal(dirname(path),fs.realpathSync(tmpdir()));assert.match(basename(path),/^wss-job-log-(test|link)-/);fs.rmSync(path,{recursive:true,force:true});
}
const read=g=>jobLogBytes(g,repository,job);
const rejects=g=>assert.throws(()=>read(g),error=>{
  assert.equal(error.message,'Bounded GitHub job log download failed');
  assert.equal(error.stdout,undefined);assert.equal(error.stderr,undefined);assert.equal(error.cause,undefined);return true;
});
for(const [name,bytes] of [
  ['empty',Buffer.alloc(0)],['binary Unicode CRLF NUL',Buffer.from('alpha\r\n\u03a9\u{1f642}\0\n\xff')],
  ['ESC before 512',Buffer.concat([Buffer.from('\x1b[31m'),Buffer.alloc(1024,65)])],
  ['ESC after 512',Buffer.concat([Buffer.alloc(1024,65),Buffer.from('\x1b[0m')])],
  ['non-UTF8',Buffer.from([0xff,0xfe,0x80,0,0x1b,13,10])],
])test(`production sync log path preserves ${name} exact bytes and hash`,()=>{
  const f=fixture({responses:[{bytes:bytes.toString('base64'),headers:{'content-length':String(bytes.length)}}]},read);
  assert.deepEqual(f.result,bytes);assert.equal(digest(f.result),digest(bytes));assert.equal(f.launched,1);
  assert.equal(f.requests[0].url,`https://api.github.com/repos/${repository}/actions/jobs/${job}/logs`);
  assert.equal(f.requests[0].headers.Authorization,`Bearer ${token}`);assert.equal(f.requests[0].rejectUnauthorized,true);assert.equal(f.requests[0].method,'GET');
});
test('production path accepts the full 64 MiB bound without terminal bytes',()=>{
  const f=fixture({responses:[{chunkSize:1024*1024,chunks:64}]},read);
  assert.equal(f.result.length,64*1024*1024);assert.equal(digest(f.result),digest(Buffer.alloc(64*1024*1024,65)));
  assert.deepEqual(f.outputs,['{"bytes":67108864}\n']);
});
test('one temporary HTTPS redirect strips all GitHub authorization and API headers',()=>{
  const location='https://temporary.example.test/log?sig=fake-signed-location',bytes=Buffer.from('exact\r\n\0\x1b[32m');
  const f=fixture({responses:[{status:302,headers:{location}},{bytes:bytes.toString('base64')}]},read);
  assert.deepEqual(f.result,bytes);assert.equal(f.requests.length,2);assert.equal(f.requests[1].url,location);
  assert.deepEqual(f.requests[1].headers,{'Accept-Encoding':'identity','User-Agent':'wss-native-proof'});assert.equal(f.requests[1].rejectUnauthorized,true);
});
for(const [name,responses] of Object.entries({
  unauthorized:[{status:401}],forbidden:[{status:403}],notFound:[{status:404}],server:[{status:503}],partialStatus:[{status:206}],
  unsupportedRedirect:[{status:307,headers:{location:'https://temporary.example.test/log'}}],
  redirectLoop:[{status:302,headers:{location:'https://temporary.example.test/log'}},{status:302,headers:{location:'https://temporary.example.test/again'}}],
  missingLocation:[{status:302}],httpLocation:[{status:302,headers:{location:'http://temporary.example.test/log'}}],
  relativeLocation:[{status:302,headers:{location:'/log'}}],credentials:[{status:302,headers:{location:'https://token@temporary.example.test/log'}}],
  fragment:[{status:302,headers:{location:'https://temporary.example.test/log#data'}}],port:[{status:302,headers:{location:'https://temporary.example.test:444/log'}}],
  controlLocation:[{status:302,headers:{location:'https://temporary.example.test/log\x1b'}}],
  encoded:[{headers:{'content-encoding':'gzip'}}],oversizedHeader:[{headers:{'content-length':'67108865'}}],
  invalidLength:[{headers:{'content-length':'1, 1'}}],shortBody:[{bytes:'YQ==',headers:{'content-length':'2'}}],
  extraBody:[{bytes:'YWE=',headers:{'content-length':'1'}}],incomplete:[{complete:false}],aborted:[{aborted:true}],
  truncated:[{partial:true}],streamOverflow:[{chunkSize:1024*1024,chunks:65}],networkError:[{error:'synthetic signed URL body\x1b'}],
}))test(`production log transport rejects ${name} and cleans up`,()=>{const f=fixture({responses},rejects);assert.equal(f.launched,1);assert.equal(f.requests.length,responses.length);});
test('total HTTP deadline aborts without exposing failure details',()=>{
  const f=fixture({fastDeadline:true,responses:[{hang:true}]},rejects);assert.equal(f.requests.length,1);
});
test('total HTTP deadline covers the storage hop too',()=>{
  const f=fixture({fastDeadline:true,responses:[{status:302,headers:{location:'https://temporary.example.test/log'}},{hang:true}]},rejects);assert.equal(f.requests.length,2);
});
for(const [name,intercept] of Object.entries({
  processFailure:()=>{throw Object.assign(new Error('fake-token\x1braw-log'),{stdout:'signed URL',stderr:'body'});},
  missingBytes:()=>'{"bytes":1}\n',malformedMetadata:()=>'{"bytes":0,"url":"fake-signed-location"}\n',
  rawOutput:()=> '\x1b[31mraw-log',
}))test(`sync parent rejects ${name} and cleans up`,()=>{fixture({},rejects,{intercept});});
test('private output creation refuses collisions without overwriting',()=>{
  const f=fixture({},rejects,{prepareFile:path=>fs.writeFileSync(path,'collision',{flag:'wx'}),residual:directory=>{const file=join(directory,'body');assert.equal(fs.readFileSync(file,'utf8'),'collision');fs.unlinkSync(file);fs.rmdirSync(directory);}});assert.equal(f.launched,0);
});
test('private output creation refuses a preexisting symlink or Windows junction',()=>{
  const root=fs.mkdtempSync(join(tmpdir(),'wss-job-log-link-')),target=join(root,'target');
  if(process.platform==='win32')fs.mkdirSync(target);
  const sentinel=process.platform==='win32'?join(target,'sentinel'):target;fs.writeFileSync(sentinel,'unchanged');
  try{
    const f=fixture({},rejects,{
      prepareFile:path=>fs.symlinkSync(target,path,process.platform==='win32'?'junction':'file'),
      residual:directory=>{const file=join(directory,'body');assert.ok(fs.lstatSync(file).isSymbolicLink());if(process.platform==='win32')fs.rmdirSync(file);else fs.unlinkSync(file);fs.rmdirSync(directory);},
    });
    assert.equal(f.launched,0);assert.equal(fs.readFileSync(sentinel,'utf8'),'unchanged');
  }finally{removeFixture(root);}
});
for(const value of ['', '../repo','owner/../repo','owner/repo?x','owner/repo#x','owner/repo\r\n','owner\\repo','owner/..','https://github.com/owner/repo'])test(`repository validation rejects ${JSON.stringify(value)} before child creation`,()=>{
  const f=fixture({},g=>assert.throws(()=>jobLogBytes(g,value,job),/Bounded/));assert.equal(f.launched,0);
});
for(const value of ['',0,1,-1,1.5,'0','-1','1?x','1/../logs','1\r\n','9'.repeat(21)])test(`job validation rejects ${JSON.stringify(value)} before child creation`,()=>{
  const f=fixture({},g=>assert.throws(()=>jobLogBytes(g,repository,value),/Bounded/));assert.equal(f.launched,0);
});
test('missing or malformed reader token never falls back to local credentials',()=>{
  for(const token of [undefined,'','fake\r\nheader','x'.repeat(16385)]){
    const f=fixture({},g=>{g.environment={GH_TOKEN:token};assert.throws(()=>read(g),/Bounded/);});assert.equal(f.launched,0);
  }
});
test('canonical distribution exports the transport and existing CI runs its regression',()=>{
  const manifest=JSON.parse(fs.readFileSync(new URL('../ci/sync-tooling.json',import.meta.url)));
  assert.deepEqual(manifest.files['.github/scripts/wss-ci/job-log.mjs'],{origin:'core',path:'tools/release/job-log.mjs'});
  assert.match(fs.readFileSync(new URL('../../.github/workflows/shared-ci.yml',import.meta.url),'utf8'),/node --test tools\/release\/\*\.test\.mjs/);
  const source=fs.readFileSync(new URL('./preflight-proof.mjs',import.meta.url),'utf8');
  assert.match(source,/downloadJobLogBytes\(g,repository,jobId\)/);assert.doesNotMatch(source,/execFileSync|\['api',.*\/logs/);
});

test('connection and response header deadline is independent of the total deadline',()=>{
  const f=fixture({fastHeaders:true,responses:[{hang:true}]},rejects);assert.equal(f.requests.length,1);
});
test('stream socket inactivity is bounded independently of the total deadline',()=>{
  const f=fixture({responses:[{stall:true}]},rejects);assert.equal(f.requests.length,1);
});

test('private output directory and body grant access only to the current user',()=>{
  const f=fixture({responses:[{}]},read,{inspectPrivate:true});assert.equal(f.result.length,0);assert.equal(f.requests.length,1);
});

test('parent process timeout kills the real isolated child and cleans up',()=>{
  // Only the test harness scales the native timeout from 125 seconds to 200 ms.
  const f=fixture({responses:[{hang:true}]},rejects,{fastParent:true});
  assert.equal(f.requests.length,1);assert.deepEqual(f.errors,[{code:'ETIMEDOUT',signal:'SIGKILL'}]);
});
test('trickle bytes cannot reset the total deadline',()=>{
  const f=fixture({fastDeadline:true,responses:[{trickle:true}]},rejects);
  assert.equal(f.requests.length,1);assert.ok(f.events.filter(e=>e.event==='chunk').length>1);
  assert.equal(f.events.filter(e=>e.event==='total-start').length,1);assert.equal(f.events.filter(e=>e.event==='total-fired').length,1);
});
test('redirects share one total deadline rather than resetting it',()=>{
  const f=fixture({fastDeadline:true,responses:[{status:302,headers:{location:'https://temporary.example.test/log'}},{trickle:true}]},rejects);
  assert.equal(f.requests.length,2);assert.ok(f.events.filter(e=>e.event==='chunk').length>1);
  assert.equal(f.events.filter(e=>e.event==='total-start').length,1);assert.equal(f.events.filter(e=>e.event==='total-fired').length,1);
});
test('file replacement is rejected and cleanup preserves the unowned replacement',()=>{
  fixture({responses:[{}]},rejects,{
    afterChild:directory=>{fs.renameSync(join(directory,'body'),join(directory,'owned'));fs.writeFileSync(join(directory,'body'),'replacement');},
    residual:directory=>{assert.equal(fs.readFileSync(join(directory,'body'),'utf8'),'replacement');assert.equal(fs.statSync(join(directory,'owned')).size,0);fs.unlinkSync(join(directory,'body'));fs.unlinkSync(join(directory,'owned'));fs.rmdirSync(directory);},
  });
});
test('directory replacement is rejected without touching the redirected target',()=>{
  const root=fs.mkdtempSync(join(tmpdir(),'wss-job-log-link-')),target=join(root,'external');fs.mkdirSync(target);fs.writeFileSync(join(target,'sentinel'),'unchanged');
  try{
    fixture({responses:[{}]},rejects,{
      // Windows refuses directory renames with the inherited file handle open;
      // release that test-owned handle to model a replacement before validation.
      afterChild:(directory,fd)=>{if(process.platform==='win32')fs.closeSync(fd);fs.renameSync(directory,directory+'.owned');fs.symlinkSync(target,directory,process.platform==='win32'?'junction':'dir');},
      residual:directory=>{
        assert.equal(fs.realpathSync(directory),fs.realpathSync(target));assert.equal(fs.readFileSync(join(target,'sentinel'),'utf8'),'unchanged');
        if(process.platform==='win32')fs.rmdirSync(directory);else fs.unlinkSync(directory);
        fs.unlinkSync(join(directory+'.owned','body'));fs.rmdirSync(directory+'.owned');
      },
    });
  }finally{removeFixture(root);}
});
