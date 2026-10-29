import {appendFileSync} from 'node:fs';
import {GitHub} from './github.mjs';
import {CORE,requireThat} from './verify.mjs';
try {
  const g=new GitHub(),head=g.head(CORE);
  let run=process.env.REQUESTED_RUN;
  if(!run) {
    const matches=g.pages(`repos/${CORE}/actions/workflows/artifacts.yml/runs?branch=dev&event=push&head_sha=${head}`,'workflow_runs');
    requireThat(matches.length===1,'Current dev must have one unambiguous canonical push run');
    run=String(matches[0].id);
  }
  requireThat(/^[1-9][0-9]*$/.test(run),'Producer run must be a positive integer');
  const r=g.run(CORE,run);
  requireThat(r.path==='.github/workflows/artifacts.yml' && r.repository?.full_name===CORE && r.head_repository?.full_name===CORE && r.head_sha===head && r.head_branch==='dev' && r.event==='push' && r.status==='completed' && r.conclusion==='success','Producer is stale, incomplete or not canonical dev push');
  appendFileSync(process.env.GITHUB_OUTPUT,`run_id=${run}\n`);
} catch(error) {console.error(error.message);process.exitCode=1;}
