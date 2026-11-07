import test from 'node:test';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {mkdtempSync,existsSync,rmSync,readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,dirname} from 'node:path';
import {execFileSync} from 'node:child_process';
import {pathToFileURL,fileURLToPath} from 'node:url';
import {CORE} from './verify.mjs';
import {readinessAuthority,prepareReadiness,loadReadinessIntent,dispatchReadiness,collectReadiness,loadReadinessReport,WORKFLOW,REGISTRY,INTENT,REPORT} from './test-variable-snapshot-readiness.mjs';
import {captureLocalTestVariables,SNAPSHOT_WORKFLOW,SNAPSHOT_JOB,snapshotName,snapshotTitle} from './local-test-variable-readiness.mjs';
const sha=n=>String(n).repeat(40);
function fixture(channel='dev') {
 const env={GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:CORE,GITHUB_EVENT_NAME:'workflow_dispatch',GITHUB_REF:`refs/heads/${channel}`,GITHUB_SHA:sha(1),GITHUB_WORKFLOW_SHA:sha(1),GITHUB_WORKFLOW_REF:`${CORE}/${WORKFLOW}@refs/heads/${channel}`,GITHUB_RUN_ID:'10',GITHUB_RUN_ATTEMPT:'1',GITHUB_ACTOR_ID:'7'};
 const run={id:10,repository:{full_name:CORE},head_repository:{full_name:CORE,fork:false},event:'workflow_dispatch',path:WORKFLOW,head_sha:sha(1),head_branch:channel,run_attempt:1,status:'in_progress',pull_requests:[],actor:{id:7},triggering_actor:{id:7},workflow_id:8,run_started_at:'2026-11-07T00:00:00Z',updated_at:'2026-11-07T00:10:00Z'};
 const source={ref:`refs/heads/${channel}`,commit:sha(2),workflow:SNAPSHOT_WORKFLOW,workflow_blob:sha(3),tooling_tree:sha(4),actor_id:'123'};
 const registry={schema:'wss-test-variable-readiness-sources/v1',dev:null,main:null,[channel]:{controller_actor_ids:['7'],source}};
 const state={artifacts:[],documents:{},localRuns:[],posts:0,head:sha(2),first:run};
 const g={run:()=>run,get:path=>path.includes('/contents/')?{type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(registry)).toString('base64')}:path.includes('/attempts/')?state.first:{path:WORKFLOW,state:'active'},pages:()=>state.artifacts,artifact:(_repo,_run,name)=>{const matches=state.artifacts.filter(a=>a.name===name);assert.equal(matches.length,1);return matches[0];},downloadVerified:(_repo,a,[file])=>({[file]:Buffer.from(JSON.stringify(state.documents[a.name]))})};
 const reader={head:()=>state.head,object:(_repo,_commit,path)=>path==='.github/scripts/wss-ci'?{sha:sha(4),type:'tree',mode:'040000'}:{sha:sha(3),type:'blob',mode:'100644'},pages:path=>path.includes('/artifacts')?[state.localArtifact]:state.localRuns,run:()=>state.localRuns[0],get:()=>({path:SNAPSHOT_WORKFLOW,state:'active'}),jobs:()=>[{name:SNAPSHOT_JOB,status:'completed',conclusion:'success',run_id:20,head_sha:sha(2)}],artifact:()=>state.localArtifact,downloadVerified:()=>({'snapshot.json':Buffer.from(JSON.stringify(state.localDocument))}),command:()=>{state.posts++;if(state.uncertain)throw Error('uncertain');}};
 const auth=readinessAuthority(g,env),d=prepareReadiness(g,reader,auth);
 const publish=(name,value)=>{state.artifacts.push({id:state.artifacts.length+30,name,expired:false,created_at:'2026-11-07T00:01:00Z',workflow_run:{id:10,head_sha:sha(1)}});state.documents[name]=value;};
 const localEnv={GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:d.request.target.repository,GITHUB_EVENT_NAME:'workflow_dispatch',GITHUB_REF:source.ref,GITHUB_SHA:sha(2),GITHUB_WORKFLOW_SHA:sha(2),GITHUB_WORKFLOW_REF:`${d.request.target.repository}/${SNAPSHOT_WORKFLOW}@${source.ref}`,GITHUB_ACTOR_ID:'123',GITHUB_RUN_ATTEMPT:'1',GITHUB_RUN_ID:'20',WSS_USE_PIN_TESTS:'false'};
 state.localDocument=captureLocalTestVariables(d.request,localEnv);
 const localRun={id:20,repository:{full_name:d.request.target.repository},head_repository:{full_name:d.request.target.repository,fork:false},event:'workflow_dispatch',path:SNAPSHOT_WORKFLOW,head_branch:channel,head_sha:sha(2),run_attempt:1,actor:{id:123},triggering_actor:{id:123},pull_requests:[],display_title:snapshotTitle(d.request),workflow_id:50,status:'completed',conclusion:'success'};
 state.localArtifact={id:40,name:snapshotName(d.request),expired:false,workflow_run:{id:20,head_sha:sha(2)},digest:'sha256:'+'b'.repeat(64)};
 return {env,run,source,registry,state,g,reader,auth,d,publish,localEnv,localRun};
}
for(const channel of ['dev','main'])test(`${channel} real authority model: immutable intent, local capture, report independent load`,()=>{
 const f=fixture(channel);f.publish(INTENT,f.d);assert.deepEqual(loadReadinessIntent(f.g,f.auth),f.d);f.state.localRuns=[f.localRun];
 const report=collectReadiness(f.reader,f.d,f.auth);f.publish(REPORT,report);assert.deepEqual(loadReadinessReport(f.g,f.reader,f.auth,f.d),report);
 f.state.first={...f.run,status:'completed'};f.auth.attempt=2;f.reader.head=()=>sha(2);assert.deepEqual(loadReadinessReport(f.g,f.reader,f.auth,f.d),report);
 assert.throws(()=>prepareReadiness(f.g,f.reader,f.auth),/recovery/);
});
for(const [name,mutate] of Object.entries({fork:f=>f.run.head_repository.fork=true,PR:f=>f.run.event='pull_request',push:f=>f.run.event='push',actor:f=>f.run.actor.id=99,trigger:f=>f.run.triggering_actor.id=99,path:f=>f.run.path='.github/workflows/artifacts.yml',source:f=>f.run.head_sha=sha(8),channel:f=>f.run.head_branch='main',unregistered:f=>{const original=f.g.get;f.g.get=p=>p.includes('/actions/workflows/')?{path:WORKFLOW,state:'disabled_manually'}:original(p);},empty:f=>f.registry.dev=null,extra:f=>f.registry.other={},arbitraryRef:f=>f.env.GITHUB_REF='refs/heads/feature'}))test(`controller rejects ${name}`,()=>{const f=fixture();mutate(f);assert.throws(()=>readinessAuthority(f.g,f.env));});
for(const [name,mutate] of Object.entries({expired:f=>f.state.artifacts[0].expired=true,foreign:f=>f.state.artifacts[0].workflow_run.id=11,late:f=>{f.auth.attempt=2;f.state.first={...f.run,status:'completed'};f.state.artifacts[0].created_at='2026-11-08T00:00:00Z';},duplicate:f=>f.state.artifacts.push(f.state.artifacts[0]),missing:f=>f.state.artifacts=[],production:f=>f.state.documents[INTENT].schema='wss-test-variable-intents/v1',movedRegistry:f=>f.auth.source={...f.source,commit:sha(8)}}))test(`durable intent rejects ${name}`,()=>{const f=fixture();f.publish(INTENT,structuredClone(f.d));mutate(f);assert.throws(()=>loadReadinessIntent(f.g,f.auth));});
for(const uncertain of [false,true])test(`marker before POST, no repeat via output path; uncertain=${uncertain}`,()=>{
 const f=fixture(),dir=mkdtempSync(join(tmpdir(),'wss-readiness-'));try{f.state.uncertain=uncertain;const original=f.reader.command;f.reader.command=()=>{assert.ok(existsSync(join(dir,'wss-readiness-dispatch-10-a1.json')));return original();};const env={...f.env,RUNNER_TEMP:dir};
 if(uncertain)assert.throws(()=>dispatchReadiness(f.reader,f.d,f.auth,env),/uncertain/);else dispatchReadiness(f.reader,f.d,f.auth,env);
 assert.equal(f.state.posts,1);assert.throws(()=>dispatchReadiness(f.reader,f.d,f.auth,{...env,OUTPUT_DIRECTORY:'different'}),/EEXIST/);assert.equal(f.state.posts,1);
 }finally{rmSync(dir,{recursive:true,force:true});}
});
test('preexisting remote matching capture correlates without POST; later attempt cannot dispatch',()=>{const f=fixture(),dir=mkdtempSync(join(tmpdir(),'wss-readiness-'));try{f.state.localRuns=[f.localRun];assert.equal(dispatchReadiness(f.reader,f.d,f.auth,{...f.env,RUNNER_TEMP:dir}).dispatched,false);assert.equal(f.state.posts,0);assert.throws(()=>dispatchReadiness(f.reader,f.d,{...f.auth,attempt:2},{...f.env,RUNNER_TEMP:dir,GITHUB_RUN_ATTEMPT:'2'}),/initial/);}finally{rmSync(dir,{recursive:true,force:true});}});
test('source movement and report tampering fail before evidence accepted',()=>{const f=fixture();f.state.localRuns=[f.localRun];const report=collectReadiness(f.reader,f.d,f.auth);f.publish(REPORT,{...report,purpose:'production'});assert.throws(()=>loadReadinessReport(f.g,f.reader,f.auth,f.d));f.state.head=sha(9);assert.throws(()=>collectReadiness(f.reader,f.d,f.auth),/moved/);});
test('generated receiver-only closure executes actual readiness entrypoint with no credentials',()=>{
 const f=fixture(),dir=mkdtempSync(join(tmpdir(),'wss-readiness-capture-'));try{
 const sync=JSON.parse(readFileSync(new URL('../ci/sync-tooling.json',import.meta.url))),root=fileURLToPath(new URL('../../',import.meta.url));
 for(const [destination,entry] of Object.entries(sync.files)){if(entry.origin!=='core'||entry.repositories&&!entry.repositories.includes('Python_WSS_Integration_Lib'))continue;const output=join(dir,destination);mkdirSync(dirname(output),{recursive:true});writeFileSync(output,readFileSync(join(root,entry.path)));}
 const event=join(dir,'event.json'),output=join(dir,'snapshot.json'),githubOutput=join(dir,'github-output');writeFileSync(event,JSON.stringify({inputs:{request:JSON.stringify(f.d.request)}}));
 execFileSync(process.execPath,[join(dir,'.github/scripts/wss-ci/capture-test-variable-readiness.mjs'),output],{env:{...process.env,...f.localEnv,GITHUB_EVENT_PATH:event,GITHUB_OUTPUT:githubOutput,GH_TOKEN:'',GITHUB_TOKEN:'',WSS_GITHUB_APP_PRIVATE_KEY:''}});
 assert.equal(JSON.parse(readFileSync(output)).schema,'wss-test-variable-readiness-snapshot/v1');assert.match(readFileSync(githubOutput,'utf8'),/wss-readiness-test-variables-/);
 }finally{rmSync(dir,{recursive:true,force:true});}
});
test('production validators and capture are byte-identical to reviewed baseline; no readiness production imports',()=>{
 const root=fileURLToPath(new URL('../../',import.meta.url));
 const reviewed={"tools/release/local-test-variable-snapshot.mjs": "3f1fe61cda6686111e0509b8372bca54fb9038a010c9a354122031f2958d495f", "tools/release/campaign-variable-snapshots.mjs": "7eb7802a285e8162038c86394327953682feb4396e8209a930c1cde4db278c4c", "tools/release/campaign-selection-artifact.mjs": "208f8d81edefedb4dd185b95fd6617963e4276d07123cabe0ab1fb72d1ac500e", "tools/ci/shared/capture-test-variables.mjs": "6e2fc9d70e2650d9711dab8e7323b195f96a049549b5bb9c96aba85fe4a3a69a", "tools/ci/shared/test-variable-snapshot.yml": "0942f861c1ecb7f791752da6627d288d4bf542122dad7916e642b17ed581bf3f"};
 for(const [path,hash] of Object.entries(reviewed))assert.equal(createHash('sha256').update(readFileSync(join(root,path),'utf8').replaceAll('\r\n','\n')).digest('hex'),hash,path);
});

test('actual controller CLI authenticates persisted intent before POST and blocks second invocation, changed output and later attempts',()=>{
 const f=fixture(),dir=mkdtempSync(join(tmpdir(),'wss-readiness-cli-'));try{
 const fixtureFile=join(dir,'fixture.json'),hook=join(dir,'hook.mjs'),posts=join(dir,'posts'),script=fileURLToPath(new URL('./test-variable-snapshot-readiness.mjs',import.meta.url));
 writeFileSync(hook,`import {readFileSync,appendFileSync,existsSync} from 'node:fs';\nimport {join} from 'node:path';\nimport {GitHub} from ${JSON.stringify(new URL('./github.mjs',import.meta.url).href)};\nconst f=JSON.parse(readFileSync(process.env.READINESS_FIXTURE));\nGitHub.prototype.run=function(){return f.run};\nGitHub.prototype.get=function(path){if(path.includes('/contents/'))return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(f.registry)).toString('base64')};if(path.includes('/attempts/'))return f.first;return {path:path.includes('test-variable-readiness-capture')?${JSON.stringify(SNAPSHOT_WORKFLOW)}:${JSON.stringify(WORKFLOW)},state:'active'};};\nGitHub.prototype.pages=function(path){return path.includes('/WSSCore/')?f.artifacts:[]};\nGitHub.prototype.artifact=function(repo,run,name){const a=f.artifacts.filter(x=>x.name===name);if(a.length!==1)throw Error('missing or duplicate');return a[0]};\nGitHub.prototype.downloadVerified=function(repo,a,files){return {[files[0]]:Buffer.from(JSON.stringify(f.documents[a.name]))}};\nGitHub.prototype.head=function(){return '${sha(2)}'};\nGitHub.prototype.object=function(repo,commit,path){return path==='.github/scripts/wss-ci'?{sha:'${sha(4)}',type:'tree',mode:'040000'}:{sha:'${sha(3)}',type:'blob',mode:'100644'}};\nGitHub.prototype.command=function(args){if(!existsSync(join(process.env.RUNNER_TEMP,'wss-readiness-dispatch-10-a1.json')))throw Error('POST before marker');appendFileSync(process.env.READINESS_POSTS,JSON.stringify(args)+'\\n');if(process.env.READINESS_UNCERTAIN==='true')throw Error('uncertain POST');return '';};\n`);
 const save=()=>writeFileSync(fixtureFile,JSON.stringify({run:f.run,first:f.state.first,registry:f.registry,artifacts:f.state.artifacts,documents:f.state.documents}));save();
 const invoke=(op,out,extra={})=>execFileSync(process.execPath,['--import',pathToFileURL(hook).href,script,op,join(dir,out)],{env:{...process.env,...f.env,GH_TOKEN:'mock-core',WSS_READINESS_TOKEN:'mock-app',RUNNER_TEMP:dir,READINESS_FIXTURE:fixtureFile,READINESS_POSTS:posts,...extra},stdio:'pipe'});
 invoke('prepare','prepared');const d=JSON.parse(readFileSync(join(dir,'prepared','intent.json')));assert.equal(d.schema,'wss-test-variable-readiness-intent/v1');
 assert.throws(()=>invoke('dispatch','before-upload'));assert.equal(existsSync(posts),false);assert.equal(existsSync(join(dir,'wss-readiness-dispatch-10-a1.json')),false);
 f.publish(INTENT,d);save();assert.throws(()=>invoke('prepare','regenerate'));
 assert.throws(()=>invoke('dispatch','dispatch',{READINESS_UNCERTAIN:'true'}));assert.equal(readFileSync(posts,'utf8').trim().split('\n').length,1);
 assert.throws(()=>invoke('dispatch','different-output'));assert.equal(readFileSync(posts,'utf8').trim().split('\n').length,1);
 f.state.first={...f.run,status:'completed'};f.run.run_attempt=2;save();assert.throws(()=>invoke('dispatch','later',{GITHUB_RUN_ATTEMPT:'2'}));assert.equal(readFileSync(posts,'utf8').trim().split('\n').length,1);
 }finally{rmSync(dir,{recursive:true,force:true});}
});

test('readiness report cannot pass production paired-plan boundary',async()=>{const {assertSnapshotSourcesMatchPlans}=await import('./campaign-selection-artifact.mjs');const f=fixture();f.state.localRuns=[f.localRun];assert.throws(()=>assertSnapshotSourcesMatchPlans(collectReadiness(f.reader,f.d,f.auth),{},f.reader),/Unknown selection envelope/);});
test('workflow wiring is closed, credential-free on receiver, and never calls production controllers',()=>{
 const core=readFileSync(new URL('../../.github/workflows/test-variable-snapshot-readiness.yml',import.meta.url),'utf8'),local=readFileSync(new URL('../ci/shared/test-variable-readiness-capture.yml',import.meta.url),'utf8');
 assert.doesNotMatch(core,/permission-variables|campaign-variable-snapshots|prepare-test-selection|freezeCampaign|secrets: inherit|environment:/);
 assert.doesNotMatch(local,/secrets\.|create-github-app-token|environment:|WSS_LOCAL_TEST_VARIABLES_SCHEMA/);
 assert.match(core,/repositories: Python_WSS_Integration_Lib/);assert.match(core,/permission-actions: write/);assert.match(core,/permission-contents: read/);
 assert.equal((local.match(/vars\./g)||[]).length,3);assert.ok(core.indexOf('Authenticate controller')<core.indexOf('actions/create-github-app-token'));
 const registry=JSON.parse(readFileSync(new URL('../ci/test-variable-readiness-sources.json',import.meta.url)));assert.equal(registry.dev,null);assert.equal(registry.main,null);
});
