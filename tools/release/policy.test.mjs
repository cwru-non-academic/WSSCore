import {test} from 'node:test';
import assert from 'node:assert/strict';
import {checkTagGuard,tagPolicy,tagCreationPolicy} from './policy.mjs';
import {CORE,digest} from './verify.mjs';

function rule() {return{id:123,target:'tag',enforcement:'active',source_type:'Repository',source:CORE,
  conditions:{ref_name:{include:['refs/tags/v*'],exclude:[]}},bypass_actors:[],rules:[{type:'update'},{type:'deletion'}],updated_at:'2026-11-24T12:00:00Z'};}
test('complete exact active policy can be pinned and independently read before publication',()=>{
  const r=rule(),m={legacy_tag_guard:{ruleset_id:'123',policy_sha256:digest(tagPolicy(r))}};
  assert.deepEqual(checkTagGuard({get:()=>r},m),m.legacy_tag_guard);
});
for(const[name,mutate]of[
  ['branch rule',r=>r.target='branch'],['disabled rule',r=>r.enforcement='disabled'],['evaluate-only rule',r=>r.enforcement='evaluate'],
  ['partial v scope',r=>r.conditions.ref_name.include=['refs/tags/v0.*']],['excluded tag',r=>r.conditions.ref_name.exclude=['refs/tags/v1.*']],
  ['bypass app',r=>r.bypass_actors=[{actor_id:1,actor_type:'Integration',bypass_mode:'always'}]],['hidden bypass metadata',r=>delete r.bypass_actors],
  ['unexpected creation prohibition',r=>r.rules.push({type:'creation'})],['missing revision',r=>delete r.updated_at],['different repository',r=>r.source='other/repository'],
  ['creation-only guard',r=>r.rules=[{type:'creation'}]],['missing update prohibition',r=>r.rules=r.rules.filter(x=>x.type!=='update')],
  ['missing deletion prohibition',r=>r.rules=r.rules.filter(x=>x.type!=='deletion')],
  ['unreviewed organization policy',r=>r.source_type='Organization'],['unknown policy parameters',r=>r.rules[0].parameters={bypass:true}]
])test(`legacy guard rejects ${name}`,()=>{const r=rule();mutate(r);assert.throws(()=>tagPolicy(r));});
test('missing configuration cannot fall back to a self-attested flag',()=>{
  for(const m of [{},{legacy_tag_guard:{verified:true}},{legacy_tag_guard:{ruleset_id:null,policy_sha256:null}}])assert.throws(()=>checkTagGuard({get:()=>rule()},m),/unconfigured/);
});
test('API failure and changed live protection block without any write',()=>{
  const r=rule(),m={legacy_tag_guard:{ruleset_id:'123',policy_sha256:digest(tagPolicy(r))}};
  assert.throws(()=>checkTagGuard({get:()=>{throw new Error('permission denied');}},m),/permission denied/);
  const originalId=r.id;r.id=124;assert.throws(()=>checkTagGuard({get:()=>r},m),/changed/);r.id=originalId;
});

test('read-only projections must match the administrator-reviewed ruleset revision',()=>{
  const r=rule(),m={legacy_tag_guard:{ruleset_id:'123',policy_sha256:digest(tagPolicy(r))}};
  const {bypass_actors,...publicRule}=r;
  assert.deepEqual(checkTagGuard({get:()=>publicRule},m),m.legacy_tag_guard);
  assert.throws(()=>tagPolicy(publicRule),/no bypass/);
  assert.throws(()=>checkTagGuard({get:()=>({...publicRule,updated_at:'2026-11-24T12:01:00Z'})},m),/changed/);
  assert.throws(()=>checkTagGuard({get:()=>({...r,bypass_actors:[{actor_type:'OrganizationAdmin'}]})},m),/no bypass/);
});

test('versioned publication requires the pinned creation ruleset for the one configured App',()=>{
  const immutable=rule();
  const creation={...immutable,id:124,rules:[{type:'creation'}],bypass_actors:[{actor_id:77,actor_type:'Integration',bypass_mode:'always'}]};
  const m={legacy_tag_guard:{ruleset_id:'123',policy_sha256:digest(tagPolicy(immutable))},
    version_tag_creation_guard:{ruleset_id:'124',publisher_app_id:'77',policy_sha256:digest(tagCreationPolicy(creation,'77'))}};
  const client={get:path=>path.endsWith('/123')?immutable:creation};
  assert.deepEqual(checkTagGuard(client,m,{versioned:true}),m.legacy_tag_guard);
  assert.throws(()=>checkTagGuard(client,{legacy_tag_guard:m.legacy_tag_guard},{versioned:true}),/creation protection/);
  assert.throws(()=>tagCreationPolicy(creation,'78'),/only the WSS publisher App/);
  assert.throws(()=>tagCreationPolicy({...creation,rules:[{type:'creation'},{type:'update'}]},'77'),/differ from the reviewed policy/);
  const {bypass_actors,...publicRule}=creation;
  assert.deepEqual(checkTagGuard({get:path=>path.endsWith('/123')?immutable:publicRule},m,{versioned:true}),m.legacy_tag_guard);
  assert.throws(()=>checkTagGuard({get:path=>path.endsWith('/123')?immutable:{...publicRule,updated_at:'2026-11-24T13:02:00Z'}},m,{versioned:true}),/changed/);
});
