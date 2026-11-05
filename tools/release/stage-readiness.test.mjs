import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {freezeStage} from '../ci/dev-propagation.mjs';
import {CORE} from './verify.mjs';

// Metadata rejection fixtures only; these do not establish native runtime proof.
const policy=JSON.parse(readFileSync(new URL('../ci/dev-propagation.json',import.meta.url)));
const matlab=policy.libraries.find(target=>target.id==='matlab');
const sha=n=>n.toString(16).padStart(40,'0');
const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
function fixture(){
  return {
    head:(repo,branch='dev')=>repo===CORE?candidate.commit:branch==='main'?sha(3):sha(4),
    object:(repo,ref,path)=>({sha:repo===matlab.repository?(['native_evidence_guard','promotion_contract_test','merge_evidence_guard'].map(key=>matlab[key]).find(pin=>pin.path===path)?.sha||sha(5)):sha(5)}),
    get(path){
      const workflow=path.match(/\/actions\/workflows\/([^/]+)$/);
      if(workflow)return {path:'.github/workflows/'+workflow[1],state:'active'};
      const receiver=policy.libraries.find(target=>path.startsWith(`repos/${target.repository}/contents/.github/dev-propagation.json?`));
      const value=receiver?{schema:'wss-dev-stage-receiver/v1',core_workflow:'.github/workflows/dev-propagation.yml',fanout_workflow:receiver.fanout,staged_applications:true,applications:receiver.applications}:
        path.includes('/contents/.github/matlab-ci.json?')?{baseline_ready:true,candidate_preflight_ready:true}:null;
      assert.ok(value,'Unexpected metadata lookup: '+path);
      return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(value)).toString('base64')};
    }
  };
}
test('staging requires all four deployed receivers and their registered workflows',()=>{
  const frozen=freezeStage(fixture(),policy,candidate);
  assert.deepEqual(Object.keys(frozen.receivers).sort(),['csharp','matlab','python','unity']);
  for(const library of policy.libraries){
    const g=fixture(),get=g.get;
    g.get=path=>path===`repos/${library.repository}/actions/workflows/${library.fanout}`?{state:'disabled',path:'.github/workflows/'+library.fanout}:get(path);
    assert.throws(()=>freezeStage(g,policy,candidate),/Register/);
  }
});
test('each reviewed MATLAB guard is mandatory on both main and dev',()=>{
  for(const key of ['native_evidence_guard','promotion_contract_test','merge_evidence_guard'])for(const branch of ['main','dev']){
    const g=fixture(),object=g.object;
    g.object=(repo,ref,path)=>repo===matlab.repository&&ref===g.head(repo,branch)&&path===matlab[key].path?{sha:sha(99)}:object(repo,ref,path);
    assert.throws(()=>freezeStage(g,policy,candidate),/reviewed version/);
  }
});
test('native readiness cannot be supplied by only one MATLAB branch or repository',()=>{
  for(const repo of [matlab.repository,...matlab.applications])for(const branch of ['main','dev']){
    const g=fixture(),get=g.get;
    g.get=path=>path===`repos/${repo}/contents/.github/matlab-ci.json?ref=${branch}`?{type:'file',encoding:'base64',content:Buffer.from(JSON.stringify({baseline_ready:true,candidate_preflight_ready:false})).toString('base64')}:get(path);
    assert.throws(()=>freezeStage(g,policy,candidate),/readiness is disabled/);
  }
});
