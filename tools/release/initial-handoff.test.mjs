import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,writeFileSync,mkdtempSync,mkdirSync,cpSync,rmSync,existsSync} from 'node:fs';
import {spawnSync,execFileSync} from 'node:child_process';
import {join,resolve} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {CORE,CORE_JOBS,digest,bytesDigest} from './verify.mjs';
import {channelContext} from './channels.mjs';
import {freezeWorkflows,freezeWorkflowPair} from '../ci/workflow-freeze.mjs';
import {INITIAL_HANDOFF_PROFILES} from '../ci/dev-propagation.mjs';
import {renderInitialWorkflow} from '../ci/channel-workflows.mjs';

const root=resolve(fileURLToPath(new URL('../..',import.meta.url)));
const sha=n=>n.toString(16).padStart(40,'0');
const json=x=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(x)).toString('base64')});
const rehash=p=>{const {plan_id,...body}=p;return {...body,plan_id:digest(body)};};
function fixture(channel,active=true) {
  const temporary=mkdtempSync(join(tmpdir(),'wss initial spaces é ')),source=join(temporary,'Core source'),runner=join(temporary,'runner temp');
  mkdirSync(source);mkdirSync(runner);
  for(const path of ['tools/ci','tools/release','.github/workflows'])cpSync(join(root,path),join(source,path),{recursive:true});
  writeFileSync(join(source,'tools/ci/channel-activation.json'),JSON.stringify({schema:'wss-ci-channel-activation/v1',dev:true,main:active}));
  const git=(...args)=>execFileSync('git',['-C',source,...args],{encoding:'utf8',stdio:'pipe'}).trim();
  git('init','-q');git('add','.');git('-c','user.name=WSS fixture','-c','user.email=fixture@example.invalid','commit','-qm','isolated trusted source');
  const head=git('rev-parse','HEAD'),c={repository:CORE,commit:channel==='main'?head:sha(1),run_id:'11',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:bytesDigest(Buffer.from('fixture archive'))};
  const context=channelContext(channel),policy=JSON.parse(readFileSync(join(source,context.stagePolicy))),manifest=JSON.parse(readFileSync(join(source,context.targetPolicy)));
  const heads={},trees={};
  manifest.targets.forEach((t,i)=>{for(const [branch,n] of [['main',100+i],['dev',200+i]]){heads[t.repository+':'+branch]=sha(n);trees[t.repository+':'+sha(n)]=sha(300+i);}});
  const g={head:(repo,branch)=>repo===CORE?(branch==='main'?head:c.commit):heads[repo+':'+branch],
    get:path=>{if(path.includes(context.stagePolicy))return json(policy);if(path.includes(context.targetPolicy))return json(manifest);throw Error('Unexpected fixture policy '+path);},
    object:(repo,ref,path)=>{assert.equal(path,'.github/workflows');assert.ok(trees[repo+':'+ref]);return {type:'tree',sha:trees[repo+':'+ref]};}};
  let stage=rehash({schema:context.stagePlanSchema,...(channel==='main'?{channel}:{}),candidate:c,policy,policy_sha256:digest(policy),receivers:Object.fromEntries(policy.libraries.map(t=>[t.id,{heads:{main:heads[t.repository+':main'],dev:heads[t.repository+':dev']},trusted:{main:{},dev:{}}}]))});
  let acceptance;
  if(channel==='main') {
    acceptance=rehash({schema:context.planSchema,channel,candidate:c,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:heads[t.repository+':main'],trusted:Object.fromEntries(t.trusted_paths.map(path=>[path,sha(9)]))}]))});
    ({stage,acceptance}=freezeWorkflowPair(g,acceptance,stage));
  } else stage=freezeWorkflows(g,stage,'stage');
  const producer={id:11,workflow_id:91,repository:{full_name:CORE},head_repository:{full_name:CORE},path:'.github/workflows/artifacts.yml',head_branch:channel,head_sha:c.commit,event:'push',status:'completed',conclusion:'success',run_attempt:1};
  const data={channel,head,c,stage,acceptance,heads,trees,producer,profiles:INITIAL_HANDOFF_PROFILES,jobs:CORE_JOBS};
  const preload=join(temporary,'read only transport.mjs'),dataPath=join(temporary,'data.json'),event=join(temporary,'event.json'),output=join(temporary,'outputs'),posts=join(temporary,'posts.jsonl');
  writeFileSync(dataPath,JSON.stringify(data));
  writeFileSync(preload,`import {readFileSync,appendFileSync} from 'node:fs';
import {GitHub} from ${JSON.stringify(pathToFileURL(join(source,'tools/release/github.mjs')).href)};
const d=JSON.parse(readFileSync(${JSON.stringify(dataPath)})),mode=process.env.WSS_TEST_CASE,profile=d.profiles[process.env.WSS_TEST_FAMILY],CORE=${JSON.stringify(CORE)};
let mainReads=0,readinessReads=0;
const json=x=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(x)).toString('base64')});
GitHub.prototype.command=args=>{if(args[0]!=='api'||args[1]!=='--method'||args[2]!=='POST')throw Error('Unexpected network command');appendFileSync(${JSON.stringify(posts)},JSON.stringify(args)+'\\n');return '';};
GitHub.prototype.head=(repo,branch)=>{if(repo===CORE){if(branch==='main'){mainReads++;return mode==='core-moved'||mode==='late-core-moved'&&mainReads>=3?'f'.repeat(40):d.head;}return d.c.commit;}
  if(mode==='missing-baseline'&&repo===profile.destinations.at(-1).repository&&branch==='dev')return 'invalid';
  if(mode==='target-moved'&&repo===profile.repository&&branch===d.channel)return 'f'.repeat(40);
  return d.heads[repo+':'+branch];};
GitHub.prototype.get=path=>{
  if(path.includes('/contents/tools/ci/'+d.channel+'-propagation.json'))return json(mode==='policy'?{...d.stage.policy,channel:'other'}:d.stage.policy);
  if(path.includes('/contents/tools/release/targets.'))return json(d.acceptance.manifest);
  if(path.includes('/contents/.github/matlab-ci.json'))return json({baseline_ready:mode!=='matlab-baseline',candidate_preflight_ready:mode!=='matlab-candidate'});
  if(path.includes('/actions/workflows/')){const file=path.split('/').at(-1);return {id:file==='artifacts.yml'?91:92,path:'.github/workflows/'+file,state:mode==='unregistered'&&file==='promote-wss.yml'?'disabled_manually':'active'};}
  if(path.includes('/git/trees/')){const repository=path.split('/').slice(1,3).join('/'),destination=Object.values(d.profiles).flatMap(p=>p.destinations).find(t=>t.repository===repository);
    return {truncated:mode==='truncated',tree:destination.workflows.filter(file=>!(mode==='missing-app-workflow'&&repository===profile.destinations.at(-1).repository&&file===profile.destinations.at(-1).workflows.at(-1))).map(file=>({path:'.github/workflows/'+file,type:'blob',mode:'100644'}))};}
  if(/^repos\\/[^/]+\\/[^/]+$/.test(path)){readinessReads++;return {full_name:path.slice(6),default_branch:mode==='default-branch'||mode==='late-readiness'&&readinessReads>profile.destinations.length?'dev':'main'};}
  throw Error('Unexpected read '+path);
};
GitHub.prototype.object=(repo,ref,path)=>{if(path!=='.github/workflows')throw Error('Unexpected object');return {type:'tree',sha:mode==='workflow-drift'&&repo===profile.repository?'e'.repeat(40):d.trees[repo+':'+ref]};};
GitHub.prototype.run=()=>{const r=structuredClone(d.producer);if(mode==='producer-fork')r.head_repository.full_name='foreign/repo';if(mode==='producer-event')r.event='workflow_dispatch';if(mode==='producer-status')r.status='in_progress';if(mode==='producer-branch')r.head_branch=d.channel==='dev'?'main':'dev';if(mode==='producer-workflow')r.workflow_id=999;if(mode==='producer-attempt')r.run_attempt=2;return r;};
GitHub.prototype.jobs=()=>{const jobs=d.jobs.map(name=>({name,status:'completed',conclusion:'success'}));if(mode==='job-missing')jobs.pop();if(mode==='job-duplicate')jobs.push({...jobs[0]});if(['job-skipped','job-cancelled','job-failed'].includes(mode))jobs[0].conclusion=mode.slice(4);return jobs;};
GitHub.prototype.artifact=()=>({id:mode==='artifact-id'?13:12,name:'universal-release-assets',workflow_run:{id:mode==='artifact-run'?13:11,head_sha:mode==='artifact-source'?'e'.repeat(40):d.c.commit}});
GitHub.prototype.download=(repo,id,name)=>{if(name==='universal-release-assets')return {[d.c.archive]:Buffer.from(mode==='archive-digest'?'corrupt':'fixture archive'),'SHA256SUMS.txt':Buffer.from(d.c.sha256+'  '+d.c.archive+'\\n')};
  const p=structuredClone(name==='wss-propagation-plan'?d.acceptance:d.stage);
  if(mode==='plan-digest')p.plan_id='0'.repeat(64);if(mode==='pair'&&name!=='wss-propagation-plan')p.acceptance_plan_id='0'.repeat(64);
  return {'plan.json':Buffer.from(JSON.stringify(p))};};
GitHub.prototype.pages=()=>{if(mode==='uncertain-transport')throw Error('Uncertain transport: inspect before recovery');if(!mode.startsWith('existing'))return [];
  const r={id:71,repository:{full_name:profile.repository},head_repository:{full_name:profile.repository},path:'.github/workflows/promote-wss.yml',workflow_id:92,event:'workflow_dispatch',head_branch:d.channel,head_sha:d.heads[profile.repository+':'+d.channel],run_attempt:1,display_title:'Promote universal WSS / wss-11-12',status:mode==='existing-running'?'in_progress':'completed',conclusion:mode==='existing-failed'?'failure':'success'};
  if(mode==='existing-source')r.head_sha='e'.repeat(40);if(mode==='existing-workflow')r.workflow_id=999;return mode==='existing-duplicate'?[r,r]:[r];};
`);
  const env={...process.env,NODE_OPTIONS:'--import='+pathToFileURL(preload).href,GH_TOKEN:'fixture-read-only',WSS_WORKFLOW_READ_TOKEN:'fixture-read-only',WSS_CI_CHANNEL:channel,GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:CORE,GITHUB_EVENT_NAME:'workflow_run',GITHUB_REF:'refs/heads/main',GITHUB_SHA:head,GITHUB_WORKFLOW_SHA:head,GITHUB_RUN_ID:'51',GITHUB_RUN_ATTEMPT:'1',GITHUB_WORKSPACE:source,GITHUB_EVENT_PATH:event,RUNNER_TEMP:runner,GITHUB_OUTPUT:output};
  let sequence=0;
  function execute(family,{mode='',overrides={},entry='prepare',eventPatch={},requestPatch}={}) {
    const work=join(runner,'handoff é '+(++sequence)),request=join(temporary,'request '+sequence+'.json');
    writeFileSync(event,JSON.stringify({workflow_run:{...producer,...eventPatch}}));writeFileSync(output,'');
    writeFileSync(request,JSON.stringify(requestPatch||{ref:channel,inputs:{candidate_json:JSON.stringify(c)}}));
    const workflow=renderInitialWorkflow(family,{activation:{schema:'wss-ci-channel-activation/v1',dev:true,main:active}}).text;
    const operation=entry==='prepare'?'initial':entry==='check'?'check-initial':'dispatch-initial';
    const line=workflow.split('\n').find(line=>line.includes(` ${operation} ${family} `));
    const command=line?.match(/run: node (\S+) (\S+) (\S+) (.+)/);
    assert.ok(command,'Execute the actual generated hosted Node entrypoint');
    assert.equal(command[2],operation);assert.equal(command[3],family);
    const args=[command[1],command[2],command[3],...(entry==='prepare'?[work]:['11',...(entry==='dispatch'?[request]:[])])];
    const result=spawnSync(process.execPath,args,{cwd:source,env:{...env,GITHUB_WORKFLOW_REF:CORE+'/.github/workflows/propagate-'+family+'.yml@refs/heads/main',WSS_TEST_CASE:mode,WSS_TEST_FAMILY:family,...overrides},encoding:'utf8'});
    return {...result,work,output:readFileSync(output,'utf8'),postCount:existsSync(posts)?readFileSync(posts,'utf8').trim().split('\n').length:0};
  }
  return {temporary,source,runner,head,execute,cleanup:()=>rmSync(temporary,{recursive:true,force:true})};
}

for(const channel of ['dev','main'])test(`actual initial entrypoints preserve all three family gates on ${channel}`,()=>{
  const f=fixture(channel);
  try {
    for(const family of Object.keys(INITIAL_HANDOFF_PROFILES)) {
      const good=f.execute(family);assert.equal(good.status,0,good.stderr);assert.match(good.output,/dispatch_needed=true/);
      const body=JSON.parse(readFileSync(join(good.work,'dispatch.json')));assert.equal(body.ref,channel);assert.equal(JSON.parse(body.inputs.candidate_json).run_id,'11');
      assert.equal(good.postCount,0,'Preparation cannot write externally');
      for(const mode of ['existing-success','existing-running']){const r=f.execute(family,{mode});assert.equal(r.status,0,r.stderr);assert.match(r.output,/dispatch_needed=false/);}
      for(const mode of ['producer-fork','producer-event','producer-status','producer-branch','producer-workflow','producer-attempt','job-missing','job-duplicate','job-skipped','job-cancelled','job-failed','artifact-id','artifact-run','artifact-source','archive-digest','plan-digest','policy','workflow-drift','default-branch','truncated','missing-app-workflow','unregistered','existing-failed','existing-source','existing-workflow','existing-duplicate','uncertain-transport','target-moved','core-moved',...(family==='python'?['missing-baseline']:[]),...(family==='matlab'?['matlab-baseline','matlab-candidate']:[]),...(channel==='main'?['pair']:[])]) {
        const r=f.execute(family,{mode});assert.notEqual(r.status,0,`${family}/${mode} accepted`);assert.equal(existsSync(r.work),false,`${family}/${mode} wrote output`);assert.equal(r.postCount,0);
      }
      for(const overrides of [{WSS_CI_CHANNEL:''},{WSS_CI_CHANNEL:'other'},{WSS_CI_CHANNEL:channel==='dev'?'main':'dev'},{GITHUB_ACTIONS:'false'},{GITHUB_REPOSITORY:'foreign/repo'},{GITHUB_EVENT_NAME:'workflow_dispatch'},{GITHUB_REF:'refs/heads/dev'},{GITHUB_WORKFLOW_REF:CORE+'/.github/workflows/propagate-other.yml@refs/heads/main'},{GITHUB_SHA:sha(999)},{GITHUB_WORKFLOW_SHA:sha(999)},{GITHUB_RUN_ATTEMPT:'2'},{GITHUB_WORKSPACE:f.runner}]) {
        const r=f.execute(family,{overrides});assert.notEqual(r.status,0,JSON.stringify(overrides));assert.equal(existsSync(r.work),false);assert.equal(r.postCount,0);
      }
      const wrongEvent=f.execute(family,{eventPatch:{head_sha:sha(999)}});assert.notEqual(wrongEvent.status,0);assert.equal(wrongEvent.postCount,0);
      for(const mode of ['workflow-drift','missing-app-workflow','late-core-moved','late-readiness','existing-success']) {
        const r=f.execute(family,{mode,entry:'dispatch'});assert.notEqual(r.status,0,`${family}/${mode} dispatch accepted`);assert.equal(r.postCount,0);
      }
      const wrongRequest=f.execute(family,{entry:'dispatch',requestPatch:{ref:channel==='dev'?'main':'dev',inputs:{candidate_json:'{}'}}});assert.notEqual(wrongRequest.status,0);assert.equal(wrongRequest.postCount,0);
      const checked=f.execute(family,{entry:'check'});assert.equal(checked.status,0,checked.stderr);assert.equal(checked.postCount,0);
    }
    writeFileSync(join(f.source,'untrusted source.txt'),'dirty');
    const dirty=f.execute('python');assert.notEqual(dirty.status,0);assert.equal(existsSync(dirty.work),false);
  } finally {f.cleanup();}
});

test('inactive main refuses prepare, direct check and direct dispatch before any network mutation',()=>{
  const f=fixture('main',false);try{for(const family of Object.keys(INITIAL_HANDOFF_PROFILES))for(const entry of ['prepare','check','dispatch']){
    const r=f.execute(family,{entry});assert.notEqual(r.status,0);assert.match(r.stderr,/not activated/);assert.equal(r.postCount,0);assert.equal(existsSync(r.work),false);
  }}finally{f.cleanup();}
});

test('actual dispatch emits exactly one frozen request through the guarded mutation boundary',()=>{
  for(const channel of ['dev','main']){
    const f=fixture(channel);try{const r=f.execute('python',{entry:'dispatch'});assert.equal(r.status,0,r.stderr);assert.equal(r.postCount,1);}finally{f.cleanup();}
  }
});
