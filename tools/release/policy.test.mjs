import {test} from 'node:test';
import assert from 'node:assert/strict';
import {checkTagGuard,tagPolicy} from './policy.mjs';
import {CORE,digest} from './verify.mjs';

function rule() {return{id:123,target:'tag',enforcement:'active',source_type:'Repository',source:CORE,
  conditions:{ref_name:{include:['refs/tags/v*'],exclude:[]}},bypass_actors:[],rules:[{type:'creation'},{type:'update'},{type:'deletion'}]};}
test('complete exact active policy can be pinned and independently read before publication',()=>{
  const r=rule(),m={legacy_tag_guard:{ruleset_id:'123',policy_sha256:digest(tagPolicy(r))}};
  assert.deepEqual(checkTagGuard({get:()=>r},m),m.legacy_tag_guard);
});
for(const[name,mutate]of[
  ['branch rule',r=>r.target='branch'],['disabled rule',r=>r.enforcement='disabled'],['evaluate-only rule',r=>r.enforcement='evaluate'],
  ['partial v scope',r=>r.conditions.ref_name.include=['refs/tags/v0.*']],['excluded tag',r=>r.conditions.ref_name.exclude=['refs/tags/v1.*']],
  ['bypass app',r=>r.bypass_actors=[{actor_id:1,actor_type:'Integration',bypass_mode:'always'}]],['hidden bypass metadata',r=>delete r.bypass_actors],
  ['missing creation prohibition',r=>r.rules=r.rules.filter(x=>x.type!=='creation')],['different repository',r=>r.source='other/repository'],
  ['unreviewed organization policy',r=>r.source_type='Organization'],['unknown policy parameters',r=>r.rules[0].parameters={bypass:true}]
])test(`legacy guard rejects ${name}`,()=>{const r=rule();mutate(r);assert.throws(()=>tagPolicy(r));});
test('missing configuration cannot fall back to a self-attested flag',()=>{
  for(const m of [{},{legacy_tag_guard:{verified:true}},{legacy_tag_guard:{ruleset_id:null,policy_sha256:null}}])assert.throws(()=>checkTagGuard({get:()=>rule()},m),/unconfigured/);
});
test('API failure and changed live protection block without any write',()=>{
  const r=rule(),m={legacy_tag_guard:{ruleset_id:'123',policy_sha256:digest(tagPolicy(r))}};
  assert.throws(()=>checkTagGuard({get:()=>{throw new Error('permission denied');}},m),/permission denied/);
  r.rules.pop();assert.throws(()=>checkTagGuard({get:()=>r},m),/changed/);
});
