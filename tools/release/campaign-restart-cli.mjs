import {execFileSync} from 'node:child_process';
import {existsSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {dirname} from 'node:path';
import {GitHub} from './github.mjs';
import {canonical,requireThat} from './verify.mjs';
import {prepareRestart,restartPath,assertRestartPlans,checkRestartRunner} from './campaign-restart.mjs';
const git=(...args)=>execFileSync('git',args,{encoding:'utf8',maxBuffer:16*1024*1024}).trim();
const json=path=>JSON.parse(readFileSync(path,'utf8'));

try {
  const [command,...args]=process.argv.slice(2),g=new GitHub();
  if(command==='prepare') {
    requireThat(args.length===4,'Usage: prepare <dev|main> <successful-run> <exact-current-head> <reason>');

    requireThat(!['dev','main','HEAD'].includes(git('rev-parse','--abbrev-ref','HEAD')),'Use a dedicated request branch, never a protected channel or detached HEAD');
    const r=prepareRestart(g,{channel:args[0],runId:args[1],expectedHead:args[2],reason:args[3]}),path=restartPath(r);
    const changes=git('diff','--name-status',args[2],'HEAD').split('\n').filter(Boolean);
    requireThat(changes.length===0||(changes.length===1&&changes[0]===`A\t${path}`),'Request branch changes files beyond its single request');
    const dirty=git('status','--porcelain','--untracked-files=all').split('\n').filter(Boolean);
    requireThat(dirty.every(line=>line.slice(3)===path),'Prepare restart on a clean dedicated branch');
    if(existsSync(path)) {
      requireThat(canonical(json(path))===canonical(r),'Existing request differs; never overwrite it');
      console.log(`Already prepared ${path}; reuse the existing request branch/PR. No changes made.`);
      process.exit(0);
    }
    requireThat(changes.length===0,'Committed request missing from worktree');
    mkdirSync(dirname(path),{recursive:true});writeFileSync(path,JSON.stringify(r,null,2)+'\n',{flag:'wx'});
    console.log(`Prepared ${path}. Review and commit only this record, then open a request PR. No push, merge, dispatch, settings change, or publication performed.`);
  } else if(command==='check'||command==='check-frozen') {
    requireThat(args.length===(command==='check'?0:2),'Usage: check OR check-frozen <acceptance-plan> <stage-plan>');
    const checked=checkRestartRunner(g,{env:process.env,event:json(process.env.GITHUB_EVENT_PATH),git}),r=checked?.request;
    if(r&&command==='check-frozen'){const a=json(args[0]),s=json(args[1]);requireThat(a.candidate.commit===checked.head&&a.candidate.run_id===process.env.GITHUB_RUN_ID,'Frozen restart candidate differs from this exact runner');assertRestartPlans(r,a,s);}
    console.log(r?`Validated explicit restart ${r.request_id}`:'No new restart request in this change');
  } else throw Error('Expected prepare, check, or check-frozen');
} catch(error) {console.error(error.message);process.exitCode=1;}
