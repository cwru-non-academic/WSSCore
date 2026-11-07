import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,writeFileSync,readFileSync,rmSync,mkdirSync} from 'node:fs';
import {execFileSync} from 'node:child_process';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {resolveTestSelection} from './test-selection.mjs';
import {inspectSelectedTestCheckouts,writeSelectedTestEvidence} from './selected-test-source.mjs';
const owner='cwru-non-academic/Python_WSS_Integration_Lib',fixture='cwru-non-academic/CSharp_WSS_Integration_Lib';
const git=(path,...args)=>execFileSync('git',['-C',path,...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
function checkout(root,repository) {
  mkdirSync(root);git(root,'init');git(root,'config','user.name','Contract test');git(root,'config','user.email','contract@example.invalid');
  git(root,'remote','add','origin',`https://github.com/${repository}.git`);
  writeFileSync(join(root,'source.txt'),'exact immutable source\n');git(root,'add','.');git(root,'commit','-m','test source');
  return git(root,'rev-parse','HEAD');
}
function setup() {
  const dir=mkdtempSync(join(tmpdir(),'wss-selected-source-')),product=join(dir,'product'),tests=join(dir,'tests');
  const productSha=checkout(product,owner),testSha=checkout(tests,fixture);
  const request={channel:'dev',campaign:{repository:'cwru-non-academic/WSSCore',commit:'a'.repeat(40),run_id:'42'},targets:[{id:'python-wrapper',repository:owner,default_test_repository:fixture,allowed_test_repositories:[fixture],variables:{}}]};
  const selection=resolveTestSelection(request,{head:()=>testSha});
  return {dir,product,tests,productSha,testSha,binding:{selection,request,selection_id:selection.selection_id},roots:{selection_id:selection.selection_id,test_root:tests,products:[{path:product,repository:owner,commit:productSha}]}};
}
test('trusted bootstrap checks separate immutable test and actual product sources',()=>{
  const x=setup();try {
    const result=inspectSelectedTestCheckouts(x.binding,'python-wrapper',x.roots,x.productSha);
    assert.equal(result.evidence.test_commit,x.testSha);assert.equal(result.evidence.repository,owner);
    const output=join(x.dir,'evidence','evidence.json');
    assert.deepEqual(writeSelectedTestEvidence(x.binding,'python-wrapper',x.roots,x.productSha,output),result.evidence);
    assert.deepEqual(JSON.parse(readFileSync(output,'utf8')),result.evidence);
    assert.throws(()=>writeSelectedTestEvidence(x.binding,'python-wrapper',x.roots,x.productSha,output),/EEXIST/);
  } finally {rmSync(x.dir,{recursive:true,force:true});}
});
test('different selection, product run source, test source or repository is rejected',()=>{
  const x=setup();try {
    for(const mutate of [r=>r.selection_id='b'.repeat(64),r=>r.products[0].commit='b'.repeat(40),r=>r.products[0].repository='foreign/repo',r=>r.test_root=x.product]) {
      const roots=structuredClone(x.roots);mutate(roots);assert.throws(()=>inspectSelectedTestCheckouts(x.binding,'python-wrapper',roots,x.productSha));
    }
    git(x.tests,'remote','set-url','origin','https://github.com/foreign/repo.git');
    assert.throws(()=>inspectSelectedTestCheckouts(x.binding,'python-wrapper',x.roots,x.productSha),/repository differs/);
  } finally {rmSync(x.dir,{recursive:true,force:true});}
});
test('selected test checkout cannot contain modified, untracked or ignored source',()=>{
  for(const kind of ['modified','untracked','ignored']) {
    const x=setup();try {
      if(kind==='modified')writeFileSync(join(x.tests,'source.txt'),'changed');
      else if(kind==='untracked')writeFileSync(join(x.tests,'extra.txt'),'new');
      else {writeFileSync(join(x.tests,'.git','info','exclude'),'ignored.txt\n');writeFileSync(join(x.tests,'ignored.txt'),'hidden source');}
      assert.throws(()=>inspectSelectedTestCheckouts(x.binding,'python-wrapper',x.roots,x.productSha),/pristine/);
    } finally {rmSync(x.dir,{recursive:true,force:true});}
  }
});
test('product mutations and evidence written into a source checkout fail',()=>{
  const x=setup();try {
    assert.throws(()=>writeSelectedTestEvidence(x.binding,'python-wrapper',x.roots,x.productSha,join(x.tests,'evidence.json')),/outside source/);
    writeFileSync(join(x.product,'source.txt'),'changed');
    assert.throws(()=>inspectSelectedTestCheckouts(x.binding,'python-wrapper',x.roots,x.productSha),/Product source checkout must be clean/);
  } finally {rmSync(x.dir,{recursive:true,force:true});}
});
test('source roots must be checkout roots, not a trusted-looking subdirectory',()=>{
  const x=setup();try {
    mkdirSync(join(x.tests,'nested'));const roots={...x.roots,test_root:join(x.tests,'nested')};
    assert.throws(()=>inspectSelectedTestCheckouts(x.binding,'python-wrapper',roots,x.productSha),/exact Git checkout/);
  } finally {rmSync(x.dir,{recursive:true,force:true});}
});
