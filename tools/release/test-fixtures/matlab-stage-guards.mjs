import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {CORE} from '../verify.mjs';
import {channelContext} from '../channels.mjs';

// This fixture records Git objects, not policy expectations. Tests that corrupt a
// policy must still observe the same independent immutable owner source.
export const source=JSON.parse(readFileSync(new URL('./matlab-stage-guards.json',import.meta.url)));
export const channels=['dev','main'];
export const sha=n=>n.toString(16).padStart(40,'0');
export const pins=Object.fromEntries(Object.entries(source.reviewed).map(([role,{path,sha}])=>[role,{path,sha}]));
export const candidate={repository:CORE,commit:sha(1),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)};
export const readPolicies=()=>Object.fromEntries(channels.map(channel=>[channel,JSON.parse(readFileSync(new URL('../../ci/'+channel+'-propagation.json',import.meta.url)))]));
export const readRoles=()=>JSON.parse(readFileSync(new URL('../../ci/executable-pin-roles.json',import.meta.url)));
export const expectedProjection=()=>Object.fromEntries(channels.map(channel=>[channel,structuredClone(pins)]));
export function reviewedPolicies() {
  const policies=readPolicies();
  for(const p of Object.values(policies))Object.assign(p.libraries.find(t=>t.id==='matlab'),structuredClone(pins));
  return policies;
}
const json=value=>({type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(value)).toString('base64')});
export function guardFixture({heads=source.reviewed_sources,channel='dev',historical=false}={}) {
  const policies=readPolicies(),policy=policies[channel],context=channelContext(channel);
  const liveHeads=structuredClone(heads),calls={get:[],object:[],head:[],writes:[]};
  const objectKey=(repo,ref,path)=>JSON.stringify([repo,ref,path]);
  const objects=new Map(),commits=new Map(Object.entries(structuredClone(source.commits)));
  for(const ref of Object.values(source.reviewed_sources))for(const value of Object.values(source.reviewed))objects.set(objectKey(source.repository,ref,value.path),structuredClone(value));
  for(const ref of Object.values(heads)) {
    if(!commits.has(ref))commits.set(ref,{sha:ref,tree:{sha:sha(6)}});
    const values=historical?source.historical.pins:source.reviewed;
    for(const value of Object.values(values))objects.set(objectKey(source.repository,ref,value.path),{type:'blob',mode:'100644',...structuredClone(value)});
  }
  const g={
    head(repo,branch){calls.head.push([repo,branch]);return repo===CORE?candidate.commit:repo===source.repository?liveHeads[branch]:branch==='main'?sha(3):sha(4);},
    object(repo,ref,path){
      calls.object.push([repo,ref,path]);
      const key=objectKey(repo,ref,path);
      if(objects.has(key))return structuredClone(objects.get(key));
      if(repo===source.repository&&Object.values(pins).some(pin=>pin.path===path))assert.fail('Unexpected guard source '+ref+' / '+path);
      return {type:['.github/workflows','.github/scripts'].includes(path)?'tree':'blob',mode:'100644',sha:sha(5)};
    },
    get(path){
      calls.get.push(path);
      const commit=path.match(/^repos\/(.+)\/git\/commits\/([^/]+)$/);
      if(commit){assert.equal(commit[1],source.repository);assert.ok(commits.has(commit[2]),'Unexpected commit '+commit[2]);return structuredClone(commits.get(commit[2]));}
      if(path==='repos/'+CORE+'/contents/tools/ci/'+channel+'-propagation.json?ref='+candidate.commit)return json(policy);
      const workflow=path.match(/\/actions\/workflows\/([^/]+)$/);
      if(workflow)return {path:'.github/workflows/'+workflow[1],state:'active'};
      const receiver=policy.libraries.find(target=>path.startsWith('repos/'+target.repository+'/contents/'+context.receiverPolicy+'?'));
      if(receiver)return json({schema:context.receiverSchema,core_workflow:context.stageWorkflow,fanout_workflow:receiver.fanout,staged_applications:true,applications:receiver.applications});
      if(path.includes('/contents/.github/matlab-ci.json?'))return json({baseline_ready:true,candidate_preflight_ready:true});
      assert.fail('Unexpected metadata lookup: '+path);
    },
    command(...args){calls.writes.push(args);assert.fail('Guard checking must never write');}
  };
  return {g,policies,policy,calls,liveHeads,objects,commits,objectKey,matlab:policy.libraries.find(t=>t.id==='matlab')};
}
