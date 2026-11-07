import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {execFileSync} from 'node:child_process';
import {digest} from '../release/verify.mjs';
import {resolveTestSelection} from './test-selection.mjs';
import {selectionRequest,selectionFields} from '../release/campaign-test-selection.mjs';
const repository='cwru-non-academic/Python_WSS_Integration_Lib',targetId='python-wrapper',core='cwru-non-academic/WSSCore';
const git=(path,...args)=>execFileSync('git',['-C',path,...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
function source(path) {
  mkdirSync(path);git(path,'init');git(path,'config','user.name','Bootstrap test');git(path,'config','user.email','test@example.invalid');git(path,'remote','add','origin',`https://github.com/${repository}.git`);
  writeFileSync(join(path,'source.txt'),'immutable source\n');git(path,'add','.');git(path,'commit','-m','source');return git(path,'rev-parse','HEAD');
}
for(const campaign of [false,true])test(`actual consumer CLI select/inspect/evidence (${campaign?'campaign reload':'ordinary run'})`,()=>{
  const dir=mkdtempSync(join(tmpdir(),'wss-consumer-cli-'));
  try {
    const product=join(dir,'product'),tests=join(dir,'tests'),productSha=source(product),testSha=source(tests);
    const candidate={repository:core,commit:'a'.repeat(40),run_id:'42',artifact_id:'12',archive:'WSS-Universal-ci.zip',sha256:'b'.repeat(64)};
    const manifest=JSON.parse(readFileSync(new URL('../release/targets.dev.json',import.meta.url)));
    const policy=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url)));
    let plan;
    if(campaign) {
      manifest.test_selection_policy=policy;
      const request=selectionRequest(manifest,candidate),selection=resolveTestSelection(request,{head:()=>testSha});
      const body={schema:'wss-propagation-plan/v1',channel:'dev',candidate,manifest,manifest_sha256:digest(manifest),target_snapshots:Object.fromEntries(manifest.targets.map(t=>[t.id,{head_sha:productSha,trusted:Object.fromEntries(t.trusted_paths.map(p=>[p,'c'.repeat(40)]))}])),...selectionFields(manifest,candidate,selection)};
      plan={...body,plan_id:digest(body)};
    }
    const data={campaign,manifest,plan,candidate,testSha,log:join(dir,'network.log')},dataPath=join(dir,'fixture.json');writeFileSync(dataPath,JSON.stringify(data));
    const stub=join(dir,'mock-network.mjs');writeFileSync(stub,`
      import {readFileSync,appendFileSync} from 'node:fs';
      import {GitHub} from ${JSON.stringify(new URL('../release/github.mjs',import.meta.url).href)};
      const f=JSON.parse(readFileSync(process.env.WSS_CONSUMER_TEST_FIXTURE,'utf8'));
      GitHub.prototype.head=function(repo,branch){if(branch!=='dev')throw Error('Wrong branch');if(repo===${JSON.stringify(core)})return f.candidate.commit;if(repo===${JSON.stringify(repository)})return f.testSha;throw Error('Unexpected head read');};
      GitHub.prototype.get=function(path){
        if(path===${JSON.stringify(`repos/${repository}/actions/variables?per_page=30&page=1`)}&&!f.campaign){appendFileSync(f.log,'variables\\n');return {total_count:0,variables:[]};}
        if(path===${JSON.stringify(`repos/${core}/contents/tools/release/targets.dev.json?ref=`)}+f.candidate.commit)return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(f.manifest)).toString('base64')};
        throw Error('Unexpected API read: '+path);
      };
      GitHub.prototype.run=function(repo,id){if(repo!==${JSON.stringify(core)}||id!=='42')throw Error('Wrong producer');return {id:42,repository:{full_name:repo},head_repository:{full_name:repo},path:'.github/workflows/artifacts.yml',head_sha:f.candidate.commit,head_branch:'dev',event:'push'};};
      GitHub.prototype.download=function(repo,id,name,files){if(!f.campaign||repo!==${JSON.stringify(core)}||id!=='42'||name!=='wss-propagation-plan'||JSON.stringify(files)!=='["plan.json"]')throw Error('Unexpected artifact download');appendFileSync(f.log,'plan\\n');return {'plan.json':Buffer.from(JSON.stringify(f.plan))};};
      GitHub.prototype.command=function(){throw Error('Real network transport is forbidden by this test');};
    `);
    const script=fileURLToPath(new URL('./ci-test-selection.mjs',import.meta.url)),output=join(dir,'selection'),githubOutput=join(dir,'github-output');
    const env={...process.env,GH_TOKEN:'',WSS_TEST_VARIABLE_READ_TOKEN:campaign?'':'read-only-test-token',WSS_CONSUMER_TEST_FIXTURE:dataPath,GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:repository,GITHUB_SHA:productSha,GITHUB_RUN_ID:'99',GITHUB_RUN_ATTEMPT:'1',GITHUB_EVENT_NAME:campaign?'workflow_dispatch':'pull_request',GITHUB_OUTPUT:githubOutput,WSS_CI_CHANNEL:'dev'};
    const invoke=(args,extra={})=>execFileSync(process.execPath,['--import',pathToFileURL(stub).href,script,...args],{encoding:'utf8',env:{...env,...extra},stdio:['ignore','pipe','pipe']});
    const candidatePath=join(dir,'candidate.json');writeFileSync(candidatePath,JSON.stringify(candidate));
    invoke(['select',targetId,campaign?candidatePath:'-',output,campaign?'candidate':'committed']);
    const contextPath=join(output,'context.json'),context=JSON.parse(readFileSync(contextPath,'utf8'));
    assert.equal(context.scope,campaign?'campaign':'run');assert.match(readFileSync(githubOutput,'utf8'),new RegExp(`selection_id=${context.selection_id}`));
    const rootsPath=join(dir,'roots.json');writeFileSync(rootsPath,JSON.stringify({selection_id:context.selection_id,test_root:tests,products:[{repository,commit:productSha,path:product}]}));
    const selectedEnv={WSS_TEST_SELECTION_ID:context.selection_id};
    invoke(['inspect',targetId,contextPath,rootsPath],selectedEnv);
    const evidencePath=join(dir,'evidence.json');invoke(['evidence',targetId,contextPath,rootsPath,evidencePath],selectedEnv);
    assert.equal(JSON.parse(readFileSync(evidencePath,'utf8')).test_commit,testSha);
    assert.throws(()=>invoke(['inspect',targetId,contextPath,rootsPath],{WSS_TEST_SELECTION_ID:'d'.repeat(64)}),/prerequisite output/);
    const calls=readFileSync(data.log,'utf8').trim().split('\n');assert.deepEqual(calls,campaign?['plan','plan','plan']:['variables']);
  } finally {rmSync(dir,{recursive:true,force:true});}
});
