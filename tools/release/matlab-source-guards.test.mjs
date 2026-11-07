import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {freezeStage} from '../ci/dev-propagation.mjs';
import {channelContext} from './channels.mjs';
import {CORE} from './verify.mjs';

// Independent immutable source fixture, not values derived from the policy under test.
// Reviewed owner e5c0881852baed0447999b0386eeb1f6a72b13ea has these same blobs
// as both deployed heads below. Metadata tests do not establish native runtime proof.
const repository='cwru-non-academic/Matlab_WSS_Integration_Lib';
const heads={dev:'710e1e68370bed2683c9e32762d8d64c70067cdf',main:'08634026f13affadd41c5ac95130a01f0291db09'};
const reviewed={
  native_evidence_guard:{path:'.github/scripts/Complete-MatlabPreflight.ps1',sha:'4fb78d26cabc7605276b3119f0e8b4d4dc5ef57e'},
  promotion_contract_test:{path:'.github/scripts/tests/Test-MatlabPromotion.ps1',sha:'c6ada237074772ebe087c0999671e70e261ebe53'},
  merge_evidence_guard:{path:'.github/scripts/MatlabPromotion.ps1',sha:'d6aaeaa64ad817e98f77ffb7d919d298cbc12628'}
};
const stale={
  native_evidence_guard:['933ad63d298bb02dfaac3874910cea91f3c319a1','6a704b447b312e16fea750a96a534715ac565899'],
  merge_evidence_guard:['f36750d9a7ada6d83c2835bac6a87847b2428ab7','7e1585c6cb02dc565405a2e1c8ce1fcdca0efc1e']
};
const sha=n=>n.toString(16).padStart(40,'0');
const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
const json=value=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(value)).toString('base64')});
function fixture(channel){
  const policy=JSON.parse(readFileSync(new URL(`../ci/${channel}-propagation.json`,import.meta.url)));
  const context=channelContext(channel);
  const g={
    head:(repo,branch)=>repo===CORE?candidate.commit:repo===repository?heads[branch]:branch==='main'?sha(3):sha(4),
    object(repo,ref,path){
      if(repo===repository){
        assert.ok(Object.values(heads).includes(ref),'MATLAB source must be the exact frozen branch head');
        const pin=Object.values(reviewed).find(pin=>pin.path===path);
        if(pin)return {type:'blob',sha:pin.sha};
      }
      return {type:path==='.github/workflows'||path==='.github/scripts'?'tree':'blob',sha:sha(5)};
    },
    get(path){
      const workflow=path.match(/\/actions\/workflows\/([^/]+)$/);
      if(workflow)return {path:'.github/workflows/'+workflow[1],state:'active'};
      const receiver=policy.libraries.find(target=>path.startsWith(`repos/${target.repository}/contents/${context.receiverPolicy}?`));
      if(receiver)return json({schema:context.receiverSchema,core_workflow:context.stageWorkflow,fanout_workflow:receiver.fanout,staged_applications:true,applications:receiver.applications});
      if(path.includes('/contents/.github/matlab-ci.json?'))return json({baseline_ready:true,candidate_preflight_ready:true});
      assert.fail('Unexpected metadata lookup: '+path);
    }
  };
  return {g,policy,matlab:policy.libraries.find(target=>target.id==='matlab')};
}
for(const channel of ['dev','main']){
  test(`${channel} policy pins the independently reviewed MATLAB source and accepts both deployed heads`,()=>{
    const {g,policy,matlab}=fixture(channel);
    for(const [key,pin] of Object.entries(reviewed))assert.deepEqual(matlab[key],pin);
    const frozen=freezeStage(g,policy,candidate,{freeze:false});
    assert.deepEqual(frozen.receivers.matlab.heads,heads);
  });
  for(const [key,pin] of Object.entries(reviewed)){
    for(const branch of ['main','dev'])for(const badSha of [...(stale[key]||[]),sha(99)]){
      test(`${channel} rejects ${key} unreviewed blob ${badSha} on ${branch}`,()=>{
        const {g,policy}=fixture(channel),object=g.object;
        g.object=(repo,ref,path)=>repo===repository&&ref===heads[branch]&&path===pin.path?{type:'blob',sha:badSha}:object(repo,ref,path);
        assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),/not the reviewed version/);
      });
    }
    for(const old of stale[key]||[])test(`${channel} stale ${key} policy cannot accept current reviewed source (${old})`,()=>{
      const {g,policy,matlab}=fixture(channel);matlab[key].sha=old;
      assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),/not the reviewed version/);
    });
    for(const kind of ['missing','path','hash'])test(`${channel} rejects ${key} ${kind} policy mismatch`,()=>{
      const {g,policy,matlab}=fixture(channel);
      if(kind==='missing')delete matlab[key];
      else if(kind==='path')matlab[key].path='.github/scripts/Unreviewed.ps1';
      else matlab[key].sha='not-a-blob-sha';
      assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),/Pin the/);
    });
  }
}
