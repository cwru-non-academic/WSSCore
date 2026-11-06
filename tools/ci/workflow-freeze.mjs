// Core coordinator extension. Receiver-owned target profiles remain unchanged.
import {readFileSync,existsSync,realpathSync} from 'node:fs';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import {GitHub,loadPlan} from '../release/github.mjs';
import {candidate,canonical,CORE,CORE_JOBS,digest,jobs,requireThat} from '../release/verify.mjs';
import {channelContext,releaseFingerprint} from '../release/channels.mjs';

export const SNAPSHOT_POLICY='wss-workflow-snapshots/v1';
export const MAIN_SNAPSHOT_POLICY='wss-workflow-snapshots/v2';
const PATH='.github/workflows';
const same=(a,b)=>canonical(a)===canonical(b);
const sha=x=>typeof x==='string'&&/^[a-f0-9]{40}$/.test(x);
const definition=(p,kind)=>kind==='stage'?p.policy:p.manifest;
const context=(p,kind)=>channelContext(definition(p,kind).channel);
const policyPath=(p,kind)=>kind==='stage'?context(p,kind).stagePolicy:context(p,kind).targetPolicy;

// Always consult the producer's immutable policy, even if the envelope omits
// the new flag. Rehashing an omitted requirement must never select legacy mode.
export function snapshotRequirement(g,p,kind) {
  requireThat(['stage','acceptance'].includes(kind),'Unknown workflow snapshot operation');
  candidate(p.candidate);
  const c=context(p,kind);
  requireThat(g.head(CORE,c.branch)===p.candidate.commit,'Core moved before workflow snapshot verification');
  const file=g.get(`repos/${CORE}/contents/${policyPath(p,kind)}?ref=${p.candidate.commit}`);
  requireThat(file?.type==='file'&&file.encoding==='base64','Immutable producer workflow policy unavailable');
  const reviewed=JSON.parse(Buffer.from(file.content,'base64').toString('utf8'));
  requireThat(same(reviewed,definition(p,kind)),'Workflow snapshot policy differs from immutable producer policy');
  const required=reviewed.workflow_snapshot_policy;
  requireThat(c.channel==='main'?required===MAIN_SNAPSHOT_POLICY:required===undefined||required===SNAPSHOT_POLICY,'Unknown or missing producer-bound workflow snapshot policy');
  if(required===undefined)requireThat(!Object.hasOwn(p,'workflow_snapshots'),'Unexpected workflow snapshot extension in legacy plan');
  return required!==undefined;
}
function entries(p,kind) {
  const out={};
  const c=context(p,kind);
  const add=(id,repository,branch,source,role)=>{
    const key=`${id}:${role||branch}`;
    requireThat(!Object.hasOwn(out,key),'Duplicate workflow snapshot role');
    out[key]={id,repository,branch,source,...(role?{role}:{})};
  };
  if(c.channel==='main') {
    if(kind==='stage')for(const t of p.policy.libraries) {
      const h=p.receivers[t.id].heads;
      add(t.id,t.repository,'main',h.main,'accepted-baseline');
      add(t.id,t.repository,'main',h.main,'execution-authority');
      add(t.id,t.repository,'dev',h.dev,'counterpart');
    }
    else for(const t of p.manifest.targets) {
      const source=p.target_snapshots[t.id].head_sha;
      add(t.id,t.repository,'main',source,'accepted-baseline');
      if(t.kind==='payload') {
        add(t.id,t.repository,'main',source,'execution-authority');
        add(t.id,t.repository,'dev',null,'counterpart');
      }
    }
    return out;
  }
  if(kind==='stage') {
    for(const t of p.policy.libraries)for(const branch of ['dev','main'])add(t.id,t.repository,branch,p.receivers[t.id].heads[branch]);
  } else {
    for(const t of p.manifest.targets) {
      add(t.id,t.repository,'dev',p.target_snapshots[t.id].head_sha);
      if(t.kind==='payload')add(t.id,t.repository,'main',null);
    }
  }
  return out;
}
function tree(g,repository,ref) {
  requireThat(sha(ref),'Invalid workflow snapshot source');
  const o=g.object(repository,ref,PATH);
  requireThat(o?.type==='tree'&&sha(o.sha),'Workflow snapshot must identify a complete Git directory tree');
  return o.sha;
}
export function freezeWorkflows(g,p,kind,{observations=new Map()}={}) {
  if(!snapshotRequirement(g,p,kind))return p;
  const main=context(p,kind).channel==='main';
  const targets=Object.fromEntries(Object.entries(entries(p,kind)).map(([key,t])=>{
    const physical=`${t.repository}:${t.branch}`;
    let observed=observations.get(physical);
    if(!observed) {
      const source_sha=main?g.head(t.repository,t.branch):t.source||g.head(t.repository,t.branch);
      observed={repository:t.repository,branch:t.branch,source_sha,tree_sha:tree(g,t.repository,source_sha)};
      observations.set(physical,observed);
    }
    if(main)requireThat(!t.source||t.source===observed.source_sha,'Same-branch workflow source moved during freeze');
    return [key,{...observed,...(t.role?{role:t.role}:{})}];
  }));
  if(main)for(const a of observations.values())requireThat(g.head(a.repository,a.branch)===a.source_sha,'Workflow source moved while freezing snapshots');
  requireThat(g.head(CORE,context(p,kind).branch)===p.candidate.commit,'Core moved while freezing workflow snapshots');
  const {plan_id,...body}=p;
  const extended={...body,workflow_snapshots:{schema:main?MAIN_SNAPSHOT_POLICY:SNAPSHOT_POLICY,targets}};
  return {...extended,plan_id:digest(extended)};
}
export function freezeWorkflowPair(g,acceptance,stage) {
  requireThat(acceptance.channel==='main'&&stage.policy.channel==='main'&&same(acceptance.candidate,stage.candidate),'Paired freeze requires one exact main candidate');
  const observations=new Map();
  const a=freezeWorkflows(g,acceptance,'acceptance',{observations});
  const s=freezeWorkflows(g,stage,'stage',{observations});
  assertWorkflows(g,a,'acceptance');assertWorkflows(g,s,'stage');
  for(const o of observations.values())requireThat(g.head(o.repository,o.branch)===o.source_sha,'Workflow source moved between paired plan freezes');
  return {acceptance:a,stage:s};
}
export function assertWorkflows(g,p,kind,{ids,live=true}={}) {
  if(!snapshotRequirement(g,p,kind))return false;
  const {plan_id,...body}=p;
  requireThat(plan_id===digest(body),'Workflow snapshot plan digest mismatch');
  const expected=entries(p,kind),actual=p.workflow_snapshots,main=context(p,kind).channel==='main';
  requireThat(actual?.schema===(main?MAIN_SNAPSHOT_POLICY:SNAPSHOT_POLICY)&&same(Object.keys(actual).sort(),['schema','targets'])&&
    same(Object.keys(actual.targets||{}).sort(),Object.keys(expected).sort()),'Required workflow snapshots missing, unknown or incomplete');
  requireThat(ids===undefined||Array.isArray(ids)&&ids.length>0&&new Set(ids).size===ids.length&&ids.every(id=>Object.values(expected).some(t=>t.id===id)),'Unknown workflow snapshot scope');
  const physical=new Map();
  for(const [key,t] of Object.entries(expected)) {
    const a=actual.targets[key];
    requireThat(a&&same(Object.keys(a).sort(),['branch','repository',...(main?['role']:[]),'source_sha','tree_sha'].sort())&&a.repository===t.repository&&a.branch===t.branch&&(!main||a.role===t.role)&&sha(a.source_sha)&&sha(a.tree_sha)&&(!t.source||a.source_sha===t.source),'Workflow snapshot binding mismatch');
    const location=`${a.repository}:${a.branch}`,prior=physical.get(location);
    if(main&&prior)requireThat(prior.source_sha===a.source_sha&&prior.tree_sha===a.tree_sha,'Conflicting same-branch workflow roles');
    physical.set(location,a);
    if(!live||ids&&!ids.includes(t.id))continue;
    requireThat(tree(g,a.repository,a.source_sha)===a.tree_sha,'Frozen workflow source/tree mismatch');
    const head=g.head(a.repository,a.branch);
    requireThat((head===a.source_sha?a.tree_sha:tree(g,a.repository,head))===a.tree_sha,`Workflow tree changed after freeze: ${key}`);
  }
  return true;
}
export function collectWithWorkflows(g,p,collect) {
  assertWorkflows(g,p,'acceptance');
  const accepted=collect(g,p);
  assertWorkflows(g,p,'acceptance');
  return accepted;
}
// Read-only receiver entrypoint, including direct manual dispatches. Keep the
// full workflow guard Core-owned instead of reproducing its policy in adapters.
export function receiverAuthorization(g,producerId,repository,{loadFrozenPlan=loadPlan}={}) {
  requireThat(/^[1-9][0-9]*$/.test(producerId)&&/^cwru-non-academic\/[A-Za-z0-9_]+$/.test(repository),'Invalid receiver authorization identity');
  const p=loadFrozenPlan(g,producerId);
  requireThat(p.channel==='main'&&p.candidate.run_id===producerId,'Receiver authorization requires the exact main plan');
  const matches=p.manifest.targets.filter(t=>t.repository===repository);
  requireThat(matches.length===1,'Receiver is not a unique canonical main target');
  const r=g.run(CORE,producerId);
  requireThat(r.repository?.full_name===CORE&&r.head_repository?.full_name===CORE&&r.path==='.github/workflows/artifacts.yml'&&r.head_branch==='main'&&r.head_sha===p.candidate.commit&&r.event==='push'&&r.status==='completed'&&r.conclusion==='success','Receiver requires the completed canonical main producer');
  jobs(g.jobs(CORE,r),CORE_JOBS);
  assertWorkflows(g,p,'acceptance',{ids:[matches[0].id]});
  return {schema:'wss-receiver-workflow-authorization/v1',channel:'main',plan_id:p.plan_id,target_id:matches[0].id,repository,candidate:p.candidate};
}

// Preserve the existing publisher/dispatchers. The only additional write client
// checks run at their command boundary, using a separate read-only credential.
export function guardedWriter(writer,reader,p,kind,{ids,purpose}={}) {
  requireThat(['initial','proof','stage','publication'].includes(purpose),'Unknown guarded write purpose');
  const c=context(p,kind);
  const allowed=new Set();
  if(purpose==='initial'||purpose==='stage')for(const t of p.policy.libraries.filter(t=>!ids||ids.includes(t.id))) {
    for(const name of purpose==='initial'?['promote-wss.yml']:[t.normal,t.fanout])allowed.add(`repos/${t.repository}/actions/workflows/${name}/dispatches`);
  }
  if(purpose==='proof')for(const t of p.manifest.targets.filter(t=>!ids||ids.includes(t.id))) {
    for(const name of [t.workflow.split('/').at(-1),'release-receipt.yml'])allowed.add(`repos/${t.repository}/actions/workflows/${name}/dispatches`);
  }
  return new Proxy(writer,{get(target,key,receiver){
    if(key==='command')return args=>{
      const read=args[0]==='api'&&args.length===2&&/^repos\/cwru-non-academic\//.test(args[1])||
        purpose==='publication'&&args.length===7&&args[0]==='release'&&args[1]==='download'&&args[3]==='--repo'&&args[4]===CORE&&args[5]==='--dir';
      if(!read) {
        if(purpose==='publication') {
          const at=name=>args.indexOf(name);
          const flags=['--repo','--target',...(c.kind==='beta'?['--prerelease']:[]),'--title','--notes'];
          requireThat(args[0]==='release'&&args[1]==='create'&&args[2]===releaseFingerprint(c.channel,p.candidate.run_id,p.plan_id)&&
            at('--repo')>2&&args[at('--repo')+1]===CORE&&at('--target')>2&&args[at('--target')+1]===p.candidate.commit&&
            flags.every(flag=>args.filter(a=>a===flag).length===1)&&
            args.filter(a=>a.startsWith('--')).every(a=>flags.includes(a)),'Unsupported guarded publication mutation');
        } else {
          requireThat(args[0]==='api'&&args[1]==='--method'&&args[2]==='POST'&&allowed.has(args[3]),'Unsupported guarded dispatch mutation');
          if(purpose==='initial') {
            requireThat(args.length===6&&args[4]==='--input','Initial handoff requires the verified request file');
            const t=p.policy.libraries.find(t=>args[3]===`repos/${t.repository}/actions/workflows/promote-wss.yml/dispatches`);
            initialRequest(p,t.id,JSON.parse(readFileSync(args[5])));
          } else {
            const fields={};
            requireThat(args.length>=6&&args.length%2===0,'Malformed guarded dispatch fields');
            for(let i=4;i<args.length;i+=2) {
              const split=args[i+1].indexOf('='),key=args[i+1].slice(0,split);
              requireThat(args[i]==='-f'&&split>0&&!Object.hasOwn(fields,key),'Unknown/duplicate guarded dispatch field');
              fields[key]=args[i+1].slice(split+1);
            }
            if(purpose==='proof') {
              const receipt=args[3].endsWith('/release-receipt.yml/dispatches');
              requireThat(same(fields,receipt?{ref:c.branch,'inputs[producer_run_id]':p.candidate.run_id,'inputs[plan_id]':p.plan_id}:{ref:c.branch}),'Guarded proof inputs differ from frozen plan');
            } else {
              const t=p.policy.libraries.find(t=>args[3].startsWith(`repos/${t.repository}/`));
              const normal=args[3].endsWith(`/${t.normal}/dispatches`);
              requireThat(normal?same(fields,{ref:c.branch}):same(Object.keys(fields).sort(),['inputs[barrier_attempt]','inputs[barrier_run_id]',...(c.channel==='main'?['inputs[channel]']:[]),'inputs[producer_run_id]','ref'].sort())&&
                (c.channel!=='main'||fields['inputs[channel]']==='main')&&fields.ref==='main'&&fields['inputs[producer_run_id]']===p.candidate.run_id&&/^[1-9][0-9]*$/.test(fields['inputs[barrier_run_id]'])&&/^[1-9][0-9]*$/.test(fields['inputs[barrier_attempt]']),'Guarded stage inputs differ from frozen authorization');
            }
          }
        }
        assertWorkflows(reader,p,kind,{ids});
      }
      return target.command(args);
    };
    const value=Reflect.get(target,key,receiver);
    return typeof value==='function'?value.bind(receiver):value;
  }});
}

export function initialRequest(p,family,body) {
  const t=p.policy.libraries.find(t=>t.id===family);
  requireThat(t&&['csharp','unity','python','matlab'].includes(family),'Unknown initial handoff family');
  const c=p.candidate,branch=context(p,'stage').branch;
  if(family==='csharp')requireThat(same(body,{ref:branch,inputs:{wss_run_id:c.run_id,wss_commit:c.commit,wss_artifact_id:c.artifact_id,wss_sha256:c.sha256,correlation_id:`wss-${c.run_id}-${c.artifact_id}`}}),'Initial C# handoff differs from frozen candidate');
  else requireThat(body?.ref===branch&&same(Object.keys(body).sort(),['inputs','ref'])&&same(Object.keys(body.inputs||{}),['candidate_json'])&&same(JSON.parse(body.inputs.candidate_json),c),'Initial handoff differs from frozen candidate');
  return t;
}
export function initialProducerPlan(p,family,env) {
  const c=context(p,'stage');
  requireThat(family==='csharp'&&env.GITHUB_ACTIONS==='true'&&env.GITHUB_EVENT_NAME==='push'&&env.GITHUB_REPOSITORY===CORE&&env.GITHUB_REF===c.ref&&env.GITHUB_WORKFLOW_REF===`${CORE}/.github/workflows/artifacts.yml@${c.ref}`,'Initial in-progress handoff requires canonical Core channel producer');
  requireThat(p.candidate.commit===env.GITHUB_SHA&&p.candidate.run_id===env.GITHUB_RUN_ID,'Initial handoff source/run mismatch');
  return p;
}
async function main() {
  const [command,family,source,request]=process.argv.slice(2);
  const reader=new GitHub(process.env.WSS_WORKFLOW_READ_TOKEN||process.env.GH_TOKEN);
  if(command==='check-receiver') {
    requireThat(family&&source&&!request&&process.argv.length===5,'Expected check-receiver producer-run repository');
    console.log(canonical(receiverAuthorization(reader,family,source)));return;
  }
  requireThat(['check-initial','dispatch-initial'].includes(command)&&family&&source&&(command==='check-initial'?!request:Boolean(request)),'Expected check-initial family plan-or-run or dispatch-initial family plan-or-run request.json');
  const {loadStage,validatePlan}=await import('./dev-propagation.mjs');
  let p;
  if(process.env.GITHUB_EVENT_NAME==='push') {
    p=initialProducerPlan(validatePlan(JSON.parse(readFileSync(source))),family,process.env);
  } else {
    requireThat(process.env.GITHUB_EVENT_NAME==='workflow_run'&&process.env.GITHUB_REPOSITORY===CORE&&process.env.GITHUB_REF==='refs/heads/main'&&
      process.env.GITHUB_WORKFLOW_REF===`${CORE}/.github/workflows/propagate-${family}.yml@refs/heads/main`,'Initial handoff requires registered trusted main workflow');
    p=loadStage(reader,source);
  }
  assertWorkflows(reader,p,'stage',{ids:[family]});
  if(command==='dispatch-initial') {
    requireThat(process.env.WSS_WORKFLOW_READ_TOKEN,'Initial dispatch requires separate read-only evidence token');
    const t=initialRequest(p,family,JSON.parse(readFileSync(request)));
    const writer=guardedWriter(new GitHub(),reader,p,'stage',{ids:[family],purpose:'initial'});
    writer.command(['api','--method','POST',`repos/${t.repository}/actions/workflows/promote-wss.yml/dispatches`,'--input',resolve(request)]);
  }
}
if(process.argv[1]&&existsSync(process.argv[1])&&import.meta.url===pathToFileURL(realpathSync(process.argv[1])).href)main().catch(error=>{console.error(error.message);process.exitCode=1;});
