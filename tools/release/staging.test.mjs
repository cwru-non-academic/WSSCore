import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {stagingPolicy} from './staging.mjs';
const targets=JSON.parse(readFileSync(new URL('./staging-targets.json',import.meta.url),'utf8'));
for(const [family,target] of Object.entries(targets)) test(`${family} uses the same fail-closed producer-policy boundary`,()=>{
  const receiver={schema:'wss-dev-stage-receiver/v1',core_workflow:'.github/workflows/dev-propagation.yml',staged_applications:true,fanout_workflow:target.fanout,applications:target.applications};
  const tree={sha:'a'.repeat(40),truncated:false,tree:[]};
  assert.equal(stagingPolicy({tree,file:null,receiver,family}),false);
  const policy={schema:'wss-dev-stage/v1',channel:'dev',libraries:[{id:family,...target}]};
  tree.tree=[{path:'tools/ci/dev-propagation.json',type:'blob',sha:'b'.repeat(40)}];
  const file={type:'file',encoding:'base64',sha:'b'.repeat(40),content:Buffer.from(JSON.stringify(policy)).toString('base64')};
  const ctx={tree,file,receiver,family};assert.equal(stagingPolicy(ctx),true);
  assert.throws(()=>stagingPolicy({...ctx,tree:{...tree,truncated:true}}),/Complete verified/);
  assert.throws(()=>stagingPolicy({...ctx,file:{...file,sha:'c'.repeat(40)}}),/identity/);
  assert.throws(()=>stagingPolicy({...ctx,tree:{...tree,tree:[...tree.tree,...tree.tree]}}),/Ambiguous/);
  assert.throws(()=>stagingPolicy({...ctx,receiver:{...receiver,applications:['cwru-non-academic/OTHER']}}),/receiver declaration/);
});
