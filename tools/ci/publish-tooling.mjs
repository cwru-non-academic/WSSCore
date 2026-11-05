// Explicit user-invoked synchronization command. No workflow dispatch or merge.
import {execFileSync} from 'node:child_process';
import {readFileSync,writeFileSync,mkdtempSync,rmSync} from 'node:fs';
import {resolve,dirname,join} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath} from 'node:url';
import {requireThat} from '../release/verify.mjs';

const core=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const workspace=resolve(process.argv[2]||'');
requireThat(process.argv.length===3,'Usage: node tools/ci/publish-tooling.mjs <workspace-containing-nine-checkouts>');
const run=(command,args,options={})=>execFileSync(command,args,{encoding:'utf8',maxBuffer:32*1024*1024,...options}).trim();
const git=(root,...args)=>run('git',['-C',root,...args]);
const manifest=JSON.parse(readFileSync(join(core,'tools/ci/sync-tooling.json'),'utf8'));
const commit=git(core,'rev-parse','HEAD'),anchor=`refs/heads/ci/tooling/${commit}`,branch='codex/shared-ci-reference';
requireThat(git(core,'status','--porcelain')==='','Commit Core changes before publishing');
const existing=git(core,'ls-remote','origin',anchor);
if(existing) requireThat(existing.split(/\s+/)[0]===commit,'Immutable tooling anchor differs; never force-update it');
else git(core,'-c','push.followTags=false','push','origin',`${commit}:${anchor}`);
// Verify the durable ref before producing any consumer lock. It survives feature
// branch deletion or a squash merge, while the source SHA remains authoritative.
const reference=JSON.parse(run('gh',['api',`repos/cwru-non-academic/WSSCore/git/ref/heads/ci/tooling/${commit}`]));
requireThat(reference.object.sha===commit,'Durable source anchor is unavailable');
run('node',[join(core,'tools/ci/sync-tooling.mjs'),'copy',workspace]);
const temporary=mkdtempSync(join(tmpdir(),'wss-tooling-publication-')),results=[];
try {
  for(const repository of manifest.repositories) {
    const root=join(workspace,repository);
    requireThat(git(root,'branch','--show-current')===branch,`Use the existing reviewed draft branch in ${repository}`);
    const parent=repository==='CLI_Python_WSS_Application'||repository==='ROS_Python_WSS_Application'?'Python_WSS_Integration_Lib':repository==='CLI_Matlab_WSS_Application'?'Matlab_WSS_Integration_Lib':null;
    if(parent) {
      const parentRoot=join(workspace,parent),parentCommit=git(parentRoot,'rev-parse','HEAD');
      const workflow=join(root,'.github/workflows/release-receipt.yml');
      const original=readFileSync(workflow,'utf8');
      const pattern=new RegExp(`(repository: cwru-non-academic/${parent}\\r?\\n\\s+ref:) [^\\r\\n]+`);
      requireThat(pattern.test(original),'Missing pinned receipt adapter checkout');
      writeFileSync(workflow,original.replace(pattern,`$1 refs/heads/ci/tooling/${parentCommit}`));
    }
    // Inspectable receiver adapter changes may accompany this coordinated update.
    const dirty=git(root,'status','--porcelain','--untracked-files=all').split(/\r?\n/).filter(Boolean);
    requireThat(dirty.every(line=>/^[ MADRCU?!]{1,2} \.github\//.test(line)),`Unrelated changes in ${repository}`);
    git(root,'add','.github');git(root,'diff','--cached','--check');
    if(git(root,'diff','--cached','--name-only')) git(root,'commit','-m',`Synchronize shared CI engine ${commit.slice(0,12)}`);
    const head=git(root,'rev-parse','HEAD');git(root,'-c','push.followTags=false','push','origin',`HEAD:refs/heads/${branch}`);
    if(repository==='Python_WSS_Integration_Lib'||repository==='Matlab_WSS_Integration_Lib') {
      const adapterAnchor=`refs/heads/ci/tooling/${head}`;
      const prior=git(root,'ls-remote','origin',adapterAnchor);
      if(prior) requireThat(prior.split(/\s+/)[0]===head,'Pinned adapter anchor differs');
      else git(root,'-c','push.followTags=false','push','origin',`${head}:${adapterAnchor}`);
    }
    const prs=JSON.parse(run('gh',['pr','list','--repo',`cwru-non-academic/${repository}`,'--state','open','--head',branch,'--base','dev','--json','number,url,isDraft']));
    requireThat(prs.length<=1,'Ambiguous synchronization PR');
    let url;
    const body=join(temporary,'body.txt');writeFileSync(body,`Adopt the identical Core CI engine at ${commit}, retained independently of feature-branch lifetime by ${anchor}.\n\nOne Core implementation now owns receipt orchestration, read-only GitHub evidence transport, exact candidate/plan validation, normal-CI selection, archive safety and shared process boundaries. Receiver adapters preserve native/runtime and rich paired-preflight proofs. Review this PR with Core #34 and the other eight receiver dev drafts.\n\nRun hosted CI only. Existing frozen plans are invalidated. Advance trusted-main deployments and separate wrapper-validator pins coherently after review. No merge, dispatch, producer activation or security-setting change. See Core tools/ci/SHARED_CI_MIGRATION.md for precise scope and remaining constraints.\n`);
    if(prs.length) {requireThat(prs[0].isDraft,'Synchronization PR must remain draft');url=prs[0].url;run('gh',['pr','edit',String(prs[0].number),'--repo',`cwru-non-academic/${repository}`,'--body-file',body]);}
    else {
      url=run('gh',['pr','create','--repo',`cwru-non-academic/${repository}`,'--base','dev','--head',branch,'--draft','--title','Synchronize canonical shared CI engine','--body-file',body]);
    }
    results.push({repository,head,url,source_commit:commit});
    writeFileSync(join(workspace,'shared-ci-update-results.json'),JSON.stringify({source_commit:commit,source_ref:anchor,results},null,2)+'\n');
    console.log(`${repository}: ${head} ${url}`);
  }
} finally {rmSync(temporary,{recursive:true,force:true});}
