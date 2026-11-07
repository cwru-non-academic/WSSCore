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
    const data={fault:null,campaign,manifest,plan,candidate,testSha,productSha,apiHeadSha:campaign?productSha:'e'.repeat(40),saved:join(dir,'saved.json'),log:join(dir,'network.log')},dataPath=join(dir,'fixture.json');writeFileSync(dataPath,JSON.stringify(data));
    const stub=join(dir,'mock-network.mjs');writeFileSync(stub,`
      import {readFileSync,appendFileSync} from 'node:fs';
      import {GitHub} from ${JSON.stringify(new URL('../release/github.mjs',import.meta.url).href)};
      const f=JSON.parse(readFileSync(process.env.WSS_CONSUMER_TEST_FIXTURE,'utf8'));
      GitHub.prototype.head=function(repo,branch){if(branch!=='dev')throw Error('Wrong branch');if(repo===${JSON.stringify(core)})return f.candidate.commit;if(repo===${JSON.stringify(repository)})return f.testSha;throw Error('Unexpected head read');};
      GitHub.prototype.pages=function(){return f.fault==='missing'?[]:requireSaved()?(f.fault==='duplicate'?[artifact(),artifact()]:[artifact()]):[];};
      function requireSaved(){try{return JSON.parse(readFileSync(f.saved,'utf8'));}catch(e){if(e.code==='ENOENT')return null;throw e;}}
      function artifact(){return {id:123,name:'wss-ci-test-context-python-wrapper',expired:f.fault==='expired',created_at:f.fault==='late'?'2026-01-02T00:01:00Z':'2026-01-01T00:01:00Z',workflow_run:{id:f.fault==='artifact-run'?98:99,head_sha:f.fault==='artifact-head'?'d'.repeat(40):f.apiHeadSha}};}
      GitHub.prototype.artifact=function(){return artifact();};
      function run(){return {id:99,repository:{full_name:${JSON.stringify(repository)}},head_sha:f.apiHeadSha,path:f.fault==='workflow'?'.github/workflows/other.yml':'.github/workflows/ci.yml',event:f.campaign?'workflow_dispatch':'pull_request',run_attempt:f.fault==='attempt'?7:Number(process.env.GITHUB_RUN_ATTEMPT),status:'in_progress'};}
      GitHub.prototype.get=function(path){
        if(path.endsWith('/attempts/1'))return {...run(),run_attempt:1,status:'completed',run_started_at:'2026-01-01T00:00:00Z',updated_at:'2026-01-01T00:02:00Z'};
        if(path===${JSON.stringify(`repos/${repository}/actions/variables?per_page=30&page=1`)}&&!f.campaign){appendFileSync(f.log,'variables\\n');return {total_count:0,variables:[]};}
        if(path===${JSON.stringify(`repos/${core}/contents/tools/release/targets.dev.json?ref=`)}+f.candidate.commit)return {type:'file',encoding:'base64',content:Buffer.from(JSON.stringify(f.manifest)).toString('base64')};
        throw Error('Unexpected API read: '+path);
      };
      GitHub.prototype.run=function(repo,id){if(id==='99')return run();if(repo!==${JSON.stringify(core)}||id!=='42')throw Error('Wrong producer');return {id:42,repository:{full_name:repo},head_repository:{full_name:repo},path:'.github/workflows/artifacts.yml',head_sha:f.candidate.commit,head_branch:'dev',event:'push'};};
      GitHub.prototype.download=function(repo,id,name,files){if(id==='99')return {'context.json':Buffer.from(JSON.stringify(requireSaved()))};if(!f.campaign||repo!==${JSON.stringify(core)}||id!=='42'||name!=='wss-propagation-plan'||JSON.stringify(files)!=='["plan.json"]')throw Error('Unexpected artifact download');appendFileSync(f.log,'plan\\n');return {'plan.json':Buffer.from(JSON.stringify(f.plan))};};
      GitHub.prototype.command=function(){throw Error('Real network transport is forbidden by this test');};
    `);
    const script=fileURLToPath(new URL('./ci-test-selection.mjs',import.meta.url)),output=join(dir,'selection'),githubOutput=join(dir,'github-output');
    const env={...process.env,GH_TOKEN:'',WSS_LOCAL_TEST_VARIABLES_SCHEMA:campaign?'':'wss-effective-test-variables/v1',WSS_USE_PIN_TESTS:'false',WSS_CONSUMER_TEST_FIXTURE:dataPath,GITHUB_ACTIONS:'true',GITHUB_REPOSITORY:repository,GITHUB_SHA:productSha,GITHUB_RUN_ID:'99',GITHUB_RUN_ATTEMPT:'1',GITHUB_REF:campaign?'refs/heads/dev':'refs/pull/7/merge',GITHUB_BASE_REF:campaign?'':'dev',GITHUB_WORKFLOW_REF:`${repository}/.github/workflows/ci.yml@${campaign?'refs/heads/dev':'refs/pull/7/merge'}`,GITHUB_WORKFLOW_SHA:productSha,GITHUB_EVENT_NAME:campaign?'workflow_dispatch':'pull_request',GITHUB_OUTPUT:githubOutput,WSS_CI_CHANNEL:'dev'};
    const invoke=(args,extra={})=>execFileSync(process.execPath,['--import',pathToFileURL(stub).href,script,...args],{encoding:'utf8',env:{...env,...extra},stdio:['ignore','pipe','pipe']});
    const candidatePath=join(dir,'candidate.json');writeFileSync(candidatePath,JSON.stringify(candidate));
    assert.throws(()=>invoke(['select',targetId,campaign?candidatePath:'-',join(dir,'wrong-channel'),campaign?'candidate':'committed'],{WSS_CI_CHANNEL:'main'}),/actual target branch/);
    invoke(['select',targetId,campaign?candidatePath:'-',output,campaign?'candidate':'committed']);
    assert.doesNotMatch(readFileSync(githubOutput,'utf8'),/selection_id=/);
    writeFileSync(data.saved,readFileSync(join(output,'context.json')));
    const loaded=join(dir,'loaded');invoke(['load',targetId,campaign?candidatePath:'-',loaded,campaign?'candidate':'committed']);
    writeFileSync(dataPath,JSON.stringify({...data,testSha:'f'.repeat(40)}));
    const retry=join(dir,'retry');invoke(['select',targetId,campaign?candidatePath:'-',retry,campaign?'candidate':'committed'],{GITHUB_RUN_ATTEMPT:'2',WSS_LOCAL_TEST_VARIABLES_SCHEMA:'',WSS_USE_PIN_TESTS:'invalid'});
    writeFileSync(dataPath,JSON.stringify(data));
    assert.equal(JSON.parse(readFileSync(join(retry,'context.json'))).context.selection_id,JSON.parse(readFileSync(data.saved)).context.selection_id);
    const contextPath=join(loaded,'context.json'),context=JSON.parse(readFileSync(contextPath,'utf8'));
    assert.equal(context.scope,campaign?'campaign':'run');assert.match(readFileSync(githubOutput,'utf8'),new RegExp(`selection_id=${context.selection_id}`));
    const rootsPath=join(dir,'roots.json');writeFileSync(rootsPath,JSON.stringify({selection_id:context.selection_id,test_root:tests,products:[{repository,commit:productSha,path:product}]}));
    const selectedEnv={WSS_TEST_SELECTION_ID:context.selection_id};
    invoke(['inspect',targetId,contextPath,rootsPath],selectedEnv);
    const evidencePath=join(dir,'evidence.json');invoke(['evidence',targetId,contextPath,rootsPath,evidencePath],selectedEnv);
    assert.equal(JSON.parse(readFileSync(evidencePath,'utf8')).test_commit,testSha);
    assert.throws(()=>invoke(['inspect',targetId,contextPath,rootsPath],{WSS_TEST_SELECTION_ID:'d'.repeat(64)}),/prerequisite output/);
    for(const fault of ['missing','duplicate','expired','late','artifact-run','artifact-head','workflow','attempt']) {
      writeFileSync(dataPath,JSON.stringify({...data,fault}));
      assert.throws(()=>invoke(['select',targetId,campaign?candidatePath:'-',join(dir,`bad-${fault}`),campaign?'candidate':'committed'],{GITHUB_RUN_ATTEMPT:'2',WSS_LOCAL_TEST_VARIABLES_SCHEMA:'',WSS_USE_PIN_TESTS:'invalid'}),undefined,fault);
    }
    writeFileSync(dataPath,JSON.stringify(data));
    const original=JSON.parse(readFileSync(data.saved));
    if(campaign){
      const wrongCandidate={...candidate,sha256:'c'.repeat(64)},forged=structuredClone(original);
      forged.identity.productCandidate=wrongCandidate;
      writeFileSync(candidatePath,JSON.stringify(wrongCandidate));writeFileSync(data.saved,JSON.stringify(forged));
      assert.throws(()=>invoke(['load',targetId,candidatePath,join(dir,'wrong-campaign-candidate'),'candidate'],{GITHUB_RUN_ATTEMPT:'2'}),/Product candidate differs/);
      writeFileSync(candidatePath,JSON.stringify(candidate));writeFileSync(data.saved,JSON.stringify(original));
    }
    for(const field of ['policy','commit','event','channel','targetId','mode']) {
      const forged=structuredClone(original);forged.identity[field]='tampered';writeFileSync(data.saved,JSON.stringify(forged));
      assert.throws(()=>invoke(['load',targetId,campaign?candidatePath:'-',join(dir,`bad-envelope-${field}`),campaign?'candidate':'committed'],{GITHUB_RUN_ATTEMPT:'2'}),/envelope/);
    }
    writeFileSync(data.saved,JSON.stringify({...original,attempt:2}));
    assert.throws(()=>invoke(['select',targetId,campaign?candidatePath:'-',join(dir,'bad-creator'),campaign?'candidate':'committed'],{GITHUB_RUN_ATTEMPT:'2'}),/envelope/);
    writeFileSync(data.saved,JSON.stringify(original));
    const calls=campaign?readFileSync(data.log,'utf8').trim().split('\n'):[];assert.deepEqual(calls,campaign?['plan','plan','plan','plan','plan','plan']:[]);
  } finally {rmSync(dir,{recursive:true,force:true});}
});
