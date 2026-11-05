import {test} from 'node:test';
import assert from 'node:assert/strict';
import {publishPromotion} from './promotion.mjs';
const sha=n=>String(n).repeat(40);
function fixture() {
  const calls=[],c={target_id:'python-cli',repository_root:'.',source_sha:sha(1),candidate:{repository:'cwru-non-academic/WSSCore',commit:sha(2),run_id:'1',artifact_id:'2',archive:'WSS-Universal-ci.zip',sha256:'a'.repeat(64)},branch:`automation/python-wss-1-${'a'.repeat(64)}`,allowed_paths:['external/wss-py-wrapper'],gitlinks:{'external/wss-py-wrapper':sha(3)},parent_heads:{'cwru-non-academic/Python_WSS_Integration_Lib':sha(3)},markers:['approval'],body_path:'body.md',title:'Promotion',commit_message:'Promote'};
  const env={GITHUB_ACTIONS:'true',GITHUB_REF:'refs/heads/dev',GITHUB_REPOSITORY:'cwru-non-academic/CLI_Python_WSS_Application',WSS_PUSH_TOKEN:'fixture-only'};
  const state={moved:false,refs:[],pulls:[]};
  const g={head:repo=>repo===env.GITHUB_REPOSITORY?(state.moved?sha(9):sha(1)):sha(3),pages:()=>state.pulls,get:path=>path.includes('matching-refs')?state.refs:{tree:{sha:sha(4)},parents:[{sha:sha(1)}]}};
  const git=(root,args)=>{
    calls.push(args);
    if(args[0]==='rev-parse')return sha(1);
    if(args[0]==='diff'&&args.includes('--name-only'))return args.includes('--cached')?'external/wss-py-wrapper':'';
    if(args[0]==='ls-files')return `160000 ${sha(3)} 0\texternal/wss-py-wrapper`;
    if(args[0]==='write-tree')return sha(4);
    if(args[0]==='branch')return 'feature';return '';
  };
  const writer={command:args=>{calls.push(args);return 'https://github.com/example/pull/1';}};
  return {c,env,state,g,git,writer,calls};
}
test('one publication engine pushes without force and creates only draft dev PR',async()=>{
  const f=fixture();await publishPromotion(f.c,f);
  const push=f.calls.find(args=>args.includes('push'));assert.ok(push);assert.ok(!push.includes('--force'));
  const pr=f.calls.find(args=>args[0]==='pr');assert.ok(pr.includes('--draft'));assert.equal(pr[pr.indexOf('--base')+1],'dev');
});
test('orphan branches and moved sources cannot produce a write',async()=>{
  for(const mutate of [f=>f.state.refs=[{ref:`refs/heads/${f.c.branch}`,object:{sha:sha(7)}}],f=>f.state.moved=true]) {
    const f=fixture();mutate(f);await assert.rejects(()=>publishPromotion(f.c,f));
    assert.ok(!f.calls.some(args=>args.includes('push')||args[0]==='pr'));
  }
});
