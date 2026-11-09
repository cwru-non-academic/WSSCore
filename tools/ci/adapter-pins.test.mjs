import test from 'node:test';
import assert from 'node:assert/strict';
import {workflowPins,replacePins,verifyWorkflowPins,refreshWorkflowPins} from './adapter-pins.mjs';
const sha=n=>String(n).repeat(40),core='cwru-non-academic/WSSCore',parent='cwru-non-academic/Python_WSS_Integration_Lib',repository='cwru-non-academic/CLI_Python_WSS_Application';
const profiles={wrapper:{role:'wrapper',family:'python',wrapper:'wrapper',target:{repository:parent}},app:{role:'cli',family:'python',wrapper:'wrapper',target:{repository}}};
const lock={repository,source_repository:core,source_commit:sha(1),origins:{integration:{commit:sha(2)}}};
const files={'release-receipt.yml':`steps:\r\n  - uses: actions/checkout@v4\r\n    with:\r\n      repository: ${parent}\r\n      ref: ${sha(3)}\r\n      path: tools\r\n  - uses: actions/checkout@v4\r\n    with:\r\n      repository: ${core}\r\n      ref: ${sha(1)}\r\n`};
const g={head:(_r,b)=>b==='dev'?sha(4):sha(5),object:()=>({sha:sha(6)})};
test('matching pinned validator tree/config works across distinct current dev/main commits',()=>assert.equal(verifyWorkflowPins({repository,profiles,lock,files},g).references,2));
test('copied source identity cannot hide stale executable integration or Core pins',()=>{
  const different={...g,object:(_r,ref,path)=>({sha:ref===sha(3)&&path==='.github/scripts'?sha(9):sha(6)})};
  assert.throws(()=>verifyWorkflowPins({repository,profiles,lock,files},different),/Pinned integration tooling/);
  assert.throws(()=>verifyWorkflowPins({repository,profiles,lock,files:{'release-receipt.yml':files['release-receipt.yml'].replace(sha(1),sha(8))}},g),/Core workflow pin/);
});
test('execution config and default-main differences fail even when script copies match',()=>{
  for(const path of ['.github/scripts','.github/python-ci.json','.github/dev-propagation.json','.github/main-propagation.json']){
    const changed={...g,object:(_r,ref,p)=>({sha:ref===sha(5)&&p===path?sha(8):sha(6)})};
    assert.throws(()=>verifyWorkflowPins({repository,profiles,lock,files},changed),/Pinned integration tooling/);
  }
});
test('receipt validator may not silently become a floating branch checkout',()=>{
  assert.throws(()=>verifyWorkflowPins({repository,profiles,lock,files:{'release-receipt.yml':files['release-receipt.yml'].replace(sha(3),'dev')}},g),/pin its integration validator/);
});
test('refresh preserves CRLF, runtime gitlink expressions and unrelated fixtures literally',()=>{
  const text=files['release-receipt.yml']+`  - uses: ${parent}/.github/workflows/_python-validation.yml@${sha(3)}\r\n  - uses: actions/checkout@v4\r\n    with:\r\n      repository: ${parent}\r\n      ref: \${{ inputs.wrapper_sha }}\r\n  - uses: actions/checkout@v4\r\n    with:\r\n      repository: unrelated/fixture\r\n      ref: ${sha(7)}\r\n`;
  const pins=workflowPins(text,[core,parent]);assert.equal(pins.length,3);
  const result=replacePins(text,pins,{[core]:sha(8),[parent]:sha(9)});
  assert.ok(result.includes('ref: ${{ inputs.wrapper_sha }}\r\n'));assert.ok(result.includes(`ref: ${sha(7)}`));assert.equal(result.split(sha(9)).length-1,2);assert.equal(result.split('\r\n').length,text.split('\r\n').length);
});
test('refresh refuses an integration snapshot with a different common lock',()=>{
  assert.throws(()=>refreshWorkflowPins({repository,profiles,lock,files,adapter:sha(8),adapterLock:{...lock,repository:parent,source_commit:sha(9)}}),/matching integration source/);
  const result=refreshWorkflowPins({repository,profiles,lock,files,adapter:sha(8),adapterLock:{...lock,repository:parent}});
  assert.ok(result['release-receipt.yml'].includes(sha(8)));
});
test('unregistered targets, malformed checkout layout and floating reusable workflows fail closed',()=>{
  assert.throws(()=>verifyWorkflowPins({repository:'foreign/repo',profiles,lock,files},g),/Unknown target/);
  assert.throws(()=>workflowPins(`repository: ${parent}\npath: tools\nref: ${sha(3)}\n`,[parent]),/immediately after/);
  assert.throws(()=>workflowPins(`uses: ${parent}/.github/workflows/_python-validation.yml@main\n`,[parent]),/immutable/);
});
test('libraries check their Core contract pins without inventing a parent integration',()=>{
  const data={repository:parent,profiles,lock:{...lock,repository:parent},files:{'contracts.yml':`repository: ${core}\nref: ${sha(1)}\n`}};
  assert.equal(verifyWorkflowPins(data,g).references,1);
});
test('C# runtime gitlink expressions and trusted branch checkouts stay distinct from immutable tool pins',()=>{
  const p={wrapper:{...profiles.wrapper,family:'csharp'},app:{...profiles.app,family:'csharp'}};
  const data={repository,profiles:p,lock,files:{'release-receipt.yml':`repository: ${parent}\nref: main\n`,'runtime.yml':`repository: ${parent}\nref: \${{ inputs.integration_sha }}\n`}};
  assert.equal(verifyWorkflowPins(data,{head:()=>{throw Error('Must not reinterpret runtime sources as tool pins');}}).references,0);
});
test('an immutable C# owner pin checks the real bootstrap config on BOTH branches',()=>{
  const p={wrapper:{...profiles.wrapper,family:'csharp'},app:{...profiles.app,family:'csharp'}};
  const data={repository,profiles:p,lock,files:{'promote-integration.yml':`repository: ${parent}\nref: ${sha(3)}\n`}};
  const paths=[];
  assert.equal(verifyWorkflowPins(data,{...g,object:(repo,ref,path)=>{paths.push(path);assert.notEqual(path,'.github/csharp-ci.json');return {sha:sha(6)};}}).references,1);
  assert.ok(paths.includes('.github/universal-bootstrap.json'));
  assert.throws(()=>verifyWorkflowPins(data,{...g,object:(repo,ref,path)=>({sha:ref===sha(5)&&path==='.github/universal-bootstrap.json'?sha(9):sha(6)})}),/Pinned integration tooling/);
});
test('Unity reusable workers must match the registered workflow blob as well as scripts and config',()=>{
  const p={wrapper:{...profiles.wrapper,family:'unity'},app:{...profiles.app,family:'unity'}};
  const data={repository,profiles:p,lock,files:{'ci.yml':`uses: ${parent}/.github/workflows/_unity-validation.yml@${sha(3)}\n`}};
  assert.equal(verifyWorkflowPins(data,g).references,1);
  assert.throws(()=>verifyWorkflowPins(data,{...g,object:(_r,ref,path)=>({sha:ref===sha(3)&&path.endsWith('.yml')?sha(9):sha(6)})}),/Pinned integration tooling/);
  assert.throws(()=>verifyWorkflowPins(data,{...g,object:(_r,ref,path)=>({sha:ref===sha(3)&&path==='.github/workflows'?sha(9):sha(6)})}),/Pinned integration tooling/,'An unchanged entry workflow must not hide changed nested local workflows');
});
test('a reviewed Core native fixture stays distinct from the canonical CI tooling revision',()=>{
  const owner='cwru-non-academic/CSharp_WSS_Integration_Lib';
  const fixtureText=`steps:\n  - uses: actions/checkout@v4\n    with:\n      repository: ${core}\n      ref: ${sha(7)}\n      path: .macos-ble-fixture-core\n`;
  const data={repository:owner,profiles:{wrapper:{role:'wrapper',family:'csharp',wrapper:'wrapper',target:{repository:owner}}},lock:{...lock,repository:owner},files:{'wss-compatibility.yml':fixtureText},roles:{runtime_fixtures:[{target:owner,workflow:'wss-compatibility.yml',checkout_path:'.macos-ble-fixture-core',repository:core,commit:sha(7)}]}};
  assert.equal(verifyWorkflowPins(data,g).references,1);
  assert.equal(refreshWorkflowPins(data)['wss-compatibility.yml'],fixtureText);
  const wrong={...data,files:{'wss-compatibility.yml':fixtureText.replace(sha(7),sha(1))}};
  assert.throws(()=>verifyWorkflowPins(wrong,g),/Reviewed runtime fixture pin changed/);
  assert.equal(refreshWorkflowPins(wrong)['wss-compatibility.yml'],fixtureText);
  assert.throws(()=>verifyWorkflowPins({...data,roles:{runtime_fixtures:[...data.roles.runtime_fixtures,...data.roles.runtime_fixtures]}},g),/Ambiguous executable fixture role/);
});

test('generated receipt separates immutable bootstrap, Core and native validator locks',()=>{
  const integration={repository:parent,commit:sha(7),ref:`refs/heads/ci/tooling/${sha(7)}`};
  const locked={...lock,origins:{integration}};
  const generated=`repository: ${parent}\nref: ${sha(7)}\npath: bootstrap/Python_WSS_Integration_Lib\nrepository: ${core}\nref: ${sha(1)}\npath: core\nrepository: ${parent}\nref: ${sha(3)}\npath: validator\n`;
  const data={repository,profiles,lock:locked,files:{'release-receipt.yml':generated}};
  const reader={...g,get:()=>({encoding:'base64',content:Buffer.from(JSON.stringify({...locked,repository:parent})).toString('base64')})};
  assert.equal(verifyWorkflowPins(data,reader).references,3);
  assert.throws(()=>verifyWorkflowPins({...data,files:{'release-receipt.yml':generated.replace(sha(7),sha(8))}},reader),/bootstrap differs/);
  assert.throws(()=>verifyWorkflowPins(data,{...reader,get:()=>({encoding:'base64',content:Buffer.from(JSON.stringify({...locked,repository:parent,source_commit:sha(8)})).toString('base64')})}),/another canonical source/);
  assert.equal(refreshWorkflowPins({...data,adapter:sha(8),adapterLock:{...locked,repository:parent}})['release-receipt.yml'],generated);
});

test('Python preflight pins orchestration separately from current or historical product checkouts',()=>{
 const workflow=`jobs:\n  tooling:\n    steps:\n      - uses: actions/checkout@v4\n        with:\n          ref: \${{ github.sha }}\n          path: source\n      - uses: actions/checkout@v4\n        with:\n          repository: ${parent}\n          ref: ${sha(3)}\n          path: tools\n  validate:\n    needs: tooling\n    uses: ${parent}/.github/workflows/_python-validation.yml@${sha(3)}\n    with:\n      tools_sha: \${{ needs.tooling.outputs.tools_sha }}\n      wrapper_sha: \${{ inputs.wrapper_sha }}\n`;
 const runtime=`steps:\n  - uses: actions/checkout@v4\n    with:\n      repository: ${parent}\n      ref: \${{ inputs.wrapper_sha }}\n      path: wrapper\n  - uses: actions/checkout@v4\n    with:\n      repository: ${parent}\n      ref: \${{ inputs.tools_sha }}\n      path: tools\n`;
 const data={repository,profiles,lock,files:{...files,'wss-preflight.yml':workflow,'_python-validation.yml':runtime}};
 assert.equal(verifyWorkflowPins(data,g).references,4);
 const changed=refreshWorkflowPins({...data,adapter:sha(8),adapterLock:{...lock,repository:parent}});
 assert.equal((changed['wss-preflight.yml'].match(new RegExp(sha(8),'g'))||[]).length,2);
 assert.equal(changed['_python-validation.yml'],runtime,'Authenticated runtime product/tooling arguments remain caller-bound, not rewritten as independent pins');
 assert.ok(changed['wss-preflight.yml'].includes('wrapper_sha: ${{ inputs.wrapper_sha }}'));
 for(const path of ['.github/scripts','.github/python-ci.json','.github/workflows',' .github/workflows/_python-validation.yml'.trim()]){
  assert.throws(()=>verifyWorkflowPins(data,{...g,object:(_r,ref,p)=>({sha:ref===sha(3)&&p===path?sha(9):sha(6)})}),/Pinned integration tooling/);
 }
 assert.throws(()=>workflowPins(workflow.replace(`_python-validation.yml@${sha(3)}`,'_python-validation.yml@main'),[core,parent]),/immutable/);
});
