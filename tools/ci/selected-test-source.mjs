// Trusted bootstrap for a selected test checkout. It never executes selected
// scripts, edits product source, or treats test evidence as release authority.
import {execFileSync} from 'node:child_process';
import {readFileSync,writeFileSync,realpathSync,mkdirSync,existsSync,readdirSync} from 'node:fs';
import {resolve,relative,dirname,isAbsolute,join,basename} from 'node:path';
import {GitHub,loadPlan} from '../release/github.mjs';
import {canonical,requireThat} from '../release/verify.mjs';
import {channelContext} from '../release/channels.mjs';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {verifyTestSelection,verifySelectedTestEvidence} from './test-selection.mjs';
const realpath=realpathSync.native;
function physicalPath(path) {
  let ancestor=resolve(path);const tail=[];
  while(!existsSync(ancestor)){tail.unshift(basename(ancestor));ancestor=dirname(ancestor);}
  return resolve(realpath(ancestor),...tail);
}
const fullSha=value=>typeof value==='string'&&/^[a-f0-9]{40}$/.test(value);
const git=(root,...args)=>execFileSync('git',['-C',root,...args],{encoding:'utf8',maxBuffer:16*1024*1024}).trim();
const contains=(parent,child)=>{const part=relative(parent,child);return part===''||(!part.startsWith('..'+(process.platform==='win32'?'\\':'/'))&&part!=='..'&&!isAbsolute(part));};
const remoteRepository=remote=>/^(?:https:\/\/github\.com\/|git@github\.com:)([A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+?)(?:\.git)?\/?$/.exec(remote)?.[1];
function checkout(path,repository,commit,{test=false}={}) {
  requireThat(typeof path==='string'&&path.length>0&&fullSha(commit),'Expected an explicit immutable source checkout');
  const root=realpath(path);
  requireThat(realpath(git(root,'rev-parse','--show-toplevel'))===root,'Source root must identify its exact Git checkout');
  requireThat(remoteRepository(git(root,'remote','get-url','origin'))===repository,'Source checkout repository differs from selected identity');
  requireThat(git(root,'rev-parse','HEAD')===commit,'Source checkout commit differs from selected identity');
  const tracked=git(root,'ls-files','-v','-z').split('\0').filter(Boolean);
  requireThat(tracked.every(entry=>entry.startsWith('H ')),'Source index cannot hide changes with assume-unchanged or skip-worktree flags');
  const args=['status','--porcelain','--untracked-files=all','--ignore-submodules=none'];
  if(test)args.push('--ignored');
  requireThat(git(root,...args)==='',test?'Selected test checkout must be pristine, including ignored/untracked files':'Product source checkout must be clean');
  if(!test){
    const ignored=git(root,'ls-files','--others','--ignored','--exclude-standard','-z').split('\0').filter(Boolean);
    requireThat(!ignored.some(path=>/\.(?:pyc|pyo)$/i.test(path)||path.replaceAll('\\','/').startsWith('src/')),'Ignored files cannot override executable product source');
  }
  if(test)for(const link of gitlinks(root))requireThat(!initialized(join(root,link.path)),'Selected test gitlinks must remain uninitialized; use separate frozen dependency checkouts');
  return root;
}
function gitlinks(root) {
  return git(root,'ls-files','--stage','-z').split('\0').filter(entry=>entry.startsWith('160000 ')).map(entry=>{
    const match=/^160000 ([a-f0-9]{40}) 0\t(.+)$/s.exec(entry);
    requireThat(match,'Invalid source gitlink index entry');return {commit:match[1],path:match[2]};
  });
}
const initialized=path=>existsSync(path)&&readdirSync(path).length>0;
export function inspectSelectedTestCheckouts(binding,targetId,roots,expectedProductSha) {
  const {selection,request,selection_id}=binding;
  verifyTestSelection(selection,request,selection_id);
  const selected=selection.targets[targetId];
  requireThat(selected&&roots&&roots.selection_id===selection_id&&Array.isArray(roots.products)&&roots.products.length>0&&fullSha(expectedProductSha),'Missing exact selected-test/product context');
  const own=roots.products.filter(p=>p.repository===selected.repository);
  requireThat(own.length===1&&own[0].commit===expectedProductSha,'Product identity must remain the actual target run source');
  requireThat(new Set(roots.products.map(p=>p.repository)).size===roots.products.length,'Duplicate product source identity');
  const testRoot=checkout(roots.test_root,selected.tests.repository,selected.tests.commit,{test:true});
  const products=roots.products.map(p=>({repository:p.repository,commit:p.commit,path:checkout(p.path,p.repository,p.commit)}));
  for(const product of products)requireThat(!contains(product.path,testRoot)&&!contains(testRoot,product.path),'Selected tests and product sources must use separate non-overlapping checkouts');
  for(const product of products)for(const link of gitlinks(product.path)){
    const path=join(product.path,link.path);
    if(initialized(path))requireThat(products.some(p=>p.path===realpath(path)&&p.commit===link.commit),'Every initialized product submodule must have its own exact source declaration');
  }
  const evidence={selection_id,target_id:targetId,repository:selected.repository,test_repository:selected.tests.repository,test_commit:selected.tests.commit};
  verifySelectedTestEvidence(selection,request,selection_id,targetId,evidence);
  return {evidence,test_root:testRoot,products};
}
export function writeSelectedTestEvidence(binding,targetId,roots,expectedProductSha,outputPath) {
  const checked=inspectSelectedTestCheckouts(binding,targetId,roots,expectedProductSha),path=physicalPath(outputPath);
  for(const root of [checked.test_root,...checked.products.map(p=>p.path)])requireThat(!contains(root,path),'Evidence output must be outside source checkouts');
  mkdirSync(dirname(path),{recursive:true});
  writeFileSync(path,canonical(checked.evidence)+'\n',{encoding:'utf8',flag:'wx'});
  return checked.evidence;
}
export async function loadCampaignTestBinding(g,{kind='acceptance',runId,channel,targetId,expectedCandidate}) {
  channelContext(channel);
  requireThat(['acceptance','stage'].includes(kind)&&typeof runId==='string'&&/^[1-9][0-9]*$/.test(runId),'Invalid selected-test campaign source');
  const {planTestSelection}=await import('../release/campaign-test-selection.mjs');
  const plan=kind==='acceptance'?loadPlan(g,runId):(await import('./dev-propagation.mjs')).loadStage(g,runId,{channel});
  requireThat((kind==='acceptance'?plan.channel:plan.policy.channel)===channel,'Selected-test campaign channel differs');
  if(expectedCandidate)requireThat(canonical(plan.candidate)===canonical(expectedCandidate),'Product candidate differs from the authenticated test campaign');
  const binding=planTestSelection(plan,kind);
  requireThat(binding&&Object.hasOwn(binding.selection.targets,targetId),'Campaign has no frozen selection for this target');
  return binding;
}
if(isEntrypoint(import.meta.url)) {
  const [operation,kind,runId,channel,targetId,rootsPath,outputPath,...extra]=process.argv.slice(2);
  requireThat(['describe','inspect','evidence'].includes(operation)&&extra.length===0,'Usage: selected-test-source.mjs describe|inspect|evidence <acceptance|stage> <producer-run> <dev|main> <target-id> [roots.json] [evidence.json]');
  requireThat(operation==='describe'?(!rootsPath&&!outputPath):Boolean(rootsPath)&&(operation==='evidence'?Boolean(outputPath):!outputPath),'Invalid selected-test bootstrap arguments');
  const binding=await loadCampaignTestBinding(new GitHub(),{kind,runId,channel,targetId});
  const selected=binding.selection.targets[targetId];
  requireThat(process.env.GITHUB_ACTIONS==='true'&&process.env.GITHUB_REPOSITORY===selected.repository&&fullSha(process.env.GITHUB_SHA),'Selected-test bootstrap must run in the exact target GitHub workflow');
  if(operation==='describe')console.log(JSON.stringify({selection_id:binding.selection_id,target_id:targetId,repository:selected.repository,test_repository:selected.tests.repository,test_commit:selected.tests.commit}));
  else {
    const roots=JSON.parse(readFileSync(rootsPath,'utf8'));
    const result=operation==='inspect'?inspectSelectedTestCheckouts(binding,targetId,roots,process.env.GITHUB_SHA).evidence:writeSelectedTestEvidence(binding,targetId,roots,process.env.GITHUB_SHA,outputPath);
    console.log(JSON.stringify(result));
  }
}
