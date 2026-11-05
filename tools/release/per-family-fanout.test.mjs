import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {prepareStage,executeStage,assertFamilyBarrier} from '../ci/dev-propagation.mjs';
import {CORE,digest} from './verify.mjs';

const sha=n=>n.toString(16).padStart(40,'0');
function fixture(perFamily=true){
  const policy=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
  if(perFamily)policy.application_fanout='per-integration';else delete policy.application_fanout;
  const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
  const receivers=Object.fromEntries(policy.libraries.map((t,i)=>[t.id,{heads:{main:sha(10+i),dev:sha(20+i)},trusted:{main:{'.github/scripts':sha(5)},dev:{'.github/scripts':sha(5)}}}]));
  const body={schema:'wss-dev-propagation-plan/v1',candidate,policy,policy_sha256:digest(policy),receivers};
  const p={...body,plan_id:digest(body)},state={merged:new Set(['python']),failed:new Set(),missing:new Set(),heads:{},saved:null},commands=[];
  const target=repo=>policy.libraries.find(t=>t.repository===repo);
  const head=t=>state.heads[t.id]||sha(30+policy.libraries.indexOf(t));
  const pr=t=>({number:1,merged:true,state:'closed',merged_at:'2026-01-01T00:00:00Z',merge_commit_sha:head(t),user:{type:'Bot'},merged_by:{type:'User'},base:{ref:'dev',repo:{full_name:t.repository}},head:{sha:sha(8),repo:{full_name:t.repository}}});
  const g={
    head:(repo,branch='dev')=>repo===CORE?candidate.commit:branch==='main'?receivers[target(repo).id].heads.main:head(target(repo)),
    object:()=>({sha:sha(5)}),
    get(path){
      const t=policy.libraries.find(t=>path.startsWith(`repos/${t.repository}/`));assert.ok(t,'Unexpected lookup '+path);
      if(path.includes('/contents/'))return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(state.merged.has(t.id)?{candidate}:{})).toString('base64')};
      if(path.endsWith('/pulls/1'))return pr(t);throw Error('Unexpected lookup '+path);
    },
    pages(path){
      if(path.startsWith(`repos/${CORE}/`))return [];
      const t=policy.libraries.find(t=>path.startsWith(`repos/${t.repository}/`));assert.ok(t,'Unexpected pages '+path);
      if(path.includes('/commits/'))return [pr(t)];
      if(path.includes(`/actions/workflows/${t.normal}/runs`))return state.missing.has(t.id)?[]:[{id:100+policy.libraries.indexOf(t),run_attempt:1,event:'push',created_at:'2026-01-01T00:01:00Z',repository:{full_name:t.repository},head_repository:{full_name:t.repository},head_sha:head(t),head_branch:'dev',path:`.github/workflows/${t.normal}`,status:'completed',conclusion:state.failed.has(t.id)?'failure':'success'}];
      return [];
    },
    jobs:(repo)=>target(repo).jobs.map(name=>({name,status:'completed',conclusion:'success'})),
    download:()=>({'intent.json':JSON.stringify(state.saved)}),command:args=>commands.push(args)
  };
  return {p,g,state,commands,python:policy.libraries.find(t=>t.id==='python')};
}
test('a ready integration authorizes only its linked applications before other integrations merge',()=>{
  const f=fixture(),j=prepareStage(f.g,f.p,'9',1);
  assert.equal(j.barrier.schema,'wss-integration-barrier/v2');
  assert.deepEqual(Object.keys(j.barrier.integrations),['python']);
  assert.deepEqual(j.actions.map(a=>[a.target,a.phase]),[['python','applications']]);
});
test('a failed family does not delay another ready family or authorize its own applications',()=>{
  const f=fixture();f.state.merged.add('csharp');f.state.failed.add('csharp');
  const j=prepareStage(f.g,f.p,'9',1);
  assert.deepEqual(j.actions.map(a=>a.target),['python']);assert.equal(j.blocked[0].target,'csharp');
  f.state.failed.add('python');assert.equal(prepareStage(f.g,f.p,'9',1).actions.length,0);
});
test('committed CI can be requested for one merged family while other integrations remain pending',()=>{
  const f=fixture();f.state.missing.add('python');const j=prepareStage(f.g,f.p,'9',1);
  assert.deepEqual(j.actions.map(a=>[a.target,a.phase]),[['python','normal-ci']]);assert.equal(j.barrier,undefined);
});
test('another family becoming ready does not invalidate saved authorization for an unchanged family',()=>{
  const f=fixture(),j=prepareStage(f.g,f.p,'9',1);f.state.saved=j;f.state.merged.add('unity');
  executeStage(f.g,f.g,f.p,j);assert.equal(f.commands.length,1);assert.ok(f.commands[0].some(x=>x.includes(f.python.repository)));
});
test('the authorized family moving or a foreign barrier cannot use saved authorization',()=>{
  const f=fixture(),j=prepareStage(f.g,f.p,'9',1);f.state.saved=j;f.state.heads.python=sha(99);
  assert.throws(()=>executeStage(f.g,f.g,f.p,j),/authorized barrier|Integration moved/);assert.equal(f.commands.length,0);
  for(const change of [{plan_id:'0'.repeat(64)},{schema:'wss-integration-barrier/v1'},{emitter:{run_id:'8',attempt:1,workflow:'.github/workflows/dev-propagation.yml'}}])assert.throws(()=>assertFamilyBarrier(f.p,{...j.barrier,...change},j.barrier,f.python,'9',1),/authorized barrier/);
});
test('older frozen all-integration plans keep their original barrier policy',()=>{
  const f=fixture(false),j=prepareStage(f.g,f.p,'9',1);assert.equal(j.state,'waiting-for-integrations');assert.equal(j.actions.length,0);
});
