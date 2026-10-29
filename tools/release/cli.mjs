import {appendFileSync,readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {join,resolve} from 'node:path';
import {GitHub,archive,collect,freeze,loadPlan,publish} from './github.mjs';
import {execute,journalName,prepare} from './dispatch.mjs';
import {CORE,digest,requireThat} from './verify.mjs';
import {tagPolicy} from './policy.mjs';

const [command,...args]=process.argv.slice(2);
const g=new GitHub();
try {
  if(command==='inspect-tag-guard') {
    requireThat(args.length===1 && /^[1-9][0-9]*$/.test(args[0]),'inspect-tag-guard requires a ruleset ID');
    const policy=tagPolicy(g.get(`repos/${CORE}/rulesets/${args[0]}`));
    console.log(JSON.stringify({ruleset_id:args[0],policy_sha256:digest(policy),policy},null,2));
  } else if(command==='freeze') {
    const [candidateFile,manifestFile,destination]=args;
    requireThat(args.length===3,'freeze requires candidate.json manifest.json output directory');
    const c=JSON.parse(readFileSync(candidateFile)),m=JSON.parse(readFileSync(manifestFile));
    archive(g,c);
    const p=freeze(g,m,c);
    mkdirSync(destination,{recursive:true});writeFileSync(join(destination,'plan.json'),JSON.stringify(p,null,2)+'\n');
    console.log(`Frozen plan ${p.plan_id}`);
  } else if(command==='prepare-dispatches') {
    requireThat(args.length===2,'prepare-dispatches requires producer run ID and output directory');
    requireThat(process.env.WSS_DEV_RELEASE_ENABLED==='true','Reconciliation is not enabled');
    const p=loadPlan(g,args[0]),j=prepare(g,p,process.env.GITHUB_RUN_ID,Number(process.env.GITHUB_RUN_ATTEMPT));
    mkdirSync(args[1],{recursive:true});writeFileSync(join(args[1],'intent.json'),JSON.stringify(j,null,2)+'\n');
    appendFileSync(process.env.GITHUB_OUTPUT,`artifact_name=${journalName(p,j.run_id,j.attempt)}\n`);
  } else if(command==='dispatch') {
    requireThat(args.length===2,'dispatch requires producer run ID and saved intent.json');
    requireThat(process.env.WSS_DEV_RELEASE_ENABLED==='true' && process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/main','Dispatch runs only enabled trusted default-branch workflow');
    requireThat(process.env.WSS_JOURNAL_READ_TOKEN,'Missing read-only journal token');
    const reader=new GitHub(process.env.WSS_JOURNAL_READ_TOKEN),p=loadPlan(reader,args[0]),j=JSON.parse(readFileSync(args[1]));
    requireThat(j.run_id===process.env.GITHUB_RUN_ID && j.attempt===Number(process.env.GITHUB_RUN_ATTEMPT),'Intent is not from this workflow attempt');
    execute(g,reader,p,j);
  } else if(command==='verify' || command==='publish') {
    const [run,destination]=args;
    requireThat(args.length===2,'verify/publish requires producer run ID and output directory');
    const p=loadPlan(g,run),accepted=collect(g,p);
    mkdirSync(destination,{recursive:true});
    writeFileSync(join(destination,'acceptance.json'),JSON.stringify(accepted.record,null,2)+'\n');
    if(command==='publish') {
      requireThat(process.env.WSS_DEV_RELEASE_ENABLED==='true','Dev publication must be explicitly enabled after reviewed deployment');
      requireThat(process.env.GITHUB_REPOSITORY===CORE && process.env.GITHUB_REF==='refs/heads/main','Publisher runs only trusted default-branch workflow');
      console.log(JSON.stringify(publish(g,p,accepted,resolve(destination))));
    } else console.log(`All seven targets accepted plan ${p.plan_id}; no publication requested`);
  } else throw new Error('Expected inspect-tag-guard, freeze, prepare-dispatches, dispatch, verify or publish');
} catch(error) { console.error(error.message); process.exitCode=1; }
