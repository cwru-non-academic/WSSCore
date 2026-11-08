import test from 'node:test';
import assert from 'node:assert/strict';
import {freezeStage} from '../ci/dev-propagation.mjs';
import {channels,source,pins,sha,candidate,guardFixture} from './test-fixtures/matlab-stage-guards.mjs';

// Preserve earlier independently reviewed blobs as historical rejection input.
// These metadata tests establish no native DLL, hardware, or runtime proof.
const receiverHeads={dev:sha(101),main:sha(102)};
const stale={
  native_evidence_guard:['933ad63d298bb02dfaac3874910cea91f3c319a1','6a704b447b312e16fea750a96a534715ac565899'],
  promotion_contract_test:[source.historical.pins.promotion_contract_test.sha],
  merge_evidence_guard:['f36750d9a7ada6d83c2835bac6a87847b2428ab7','7e1585c6cb02dc565405a2e1c8ce1fcdca0efc1e',source.historical.pins.merge_evidence_guard.sha]
};
for(const channel of channels){
  test(channel+' policy accepts actual reviewed V8 owner sources independently of policy values',()=>{
    const {g,policy,matlab}=guardFixture({channel});
    for(const [key,pin] of Object.entries(pins))assert.deepEqual(matlab[key],pin);
    const frozen=freezeStage(g,policy,candidate,{freeze:false});
    assert.deepEqual(frozen.receivers.matlab.heads,source.reviewed_sources);
  });
  test(channel+' rejects preserved historical owner and deployed-head fixtures',()=>{
    for(const heads of [{dev:source.historical.owner,main:source.historical.owner},source.historical.heads]){
      const {g,policy}=guardFixture({channel,heads,historical:true});
      assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),error=>
        ['promotion_contract_test','merge_evidence_guard'].every(role=>
          channels.every(branch=>error.message.includes(role+' is not the reviewed version on '+branch))));
    }
  });
  for(const [key,pin] of Object.entries(pins)){
    for(const branch of channels)for(const badSha of [...(stale[key]||[]),sha(99)]){
      test(channel+' rejects '+key+' unreviewed blob '+badSha+' on '+branch,()=>{
        const {g,policy}=guardFixture({channel,heads:receiverHeads}),object=g.object;
        g.object=(repo,ref,path)=>repo===source.repository&&ref===receiverHeads[branch]&&path===pin.path?{type:'blob',mode:'100644',sha:badSha}:object(repo,ref,path);
        assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),new RegExp(key+' is not the reviewed version on '+branch));
      });
    }
    for(const old of stale[key]||[])test(channel+' stale '+key+' policy cannot accept reviewed source ('+old+')',()=>{
      const {g,policy,matlab}=guardFixture({channel});matlab[key].sha=old;
      assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),new RegExp(channel+' '+key+' policy is not the reviewed version'));
    });
    for(const kind of ['missing','path','hash'])test(channel+' rejects '+key+' '+kind+' policy mismatch',()=>{
      const {g,policy,matlab}=guardFixture({channel});
      if(kind==='missing')delete matlab[key];
      else if(kind==='path')matlab[key].path='.github/scripts/Unreviewed.ps1';
      else matlab[key].sha='not-a-blob-sha';
      assert.throws(()=>freezeStage(g,policy,candidate,{freeze:false}),new RegExp('Pin the registered '+key+' before handoff on '+channel));
    });
  }
}
