import test from 'node:test';
import assert from 'node:assert/strict';
import {readRepositoryTestVariables,readTestVariableSnapshots} from './repository-test-variables.mjs';
const repository='cwru-non-academic/Python_WSS_Integration_Lib';
const variable=(name,value='x')=>({name,value});
test('reads repository-only values and discards unrelated variable contents',()=>{
  const paths=[];
  const result=readRepositoryTestVariables({get:path=>{paths.push(path);return {total_count:4,variables:[variable('OTHER'),variable('WSS_USE_PIN_TESTS','true'),variable('WSS_PIN_TEST_REPOSITORY',repository),variable('WSS_PIN_TEST_COMMIT','a'.repeat(40))]};}},repository);
  assert.deepEqual(paths,[`repos/${repository}/actions/variables?per_page=30&page=1`]);
  assert.deepEqual(result,{WSS_USE_PIN_TESTS:'true',WSS_PIN_TEST_REPOSITORY:repository,WSS_PIN_TEST_COMMIT:'a'.repeat(40)});
});
test('empty successful snapshot means no repository pin variables',()=>assert.deepEqual(readRepositoryTestVariables({get:()=>({total_count:0,variables:[]})},repository),{}));
test('uses the variables API limit of thirty and consumes exact multiple final page',()=>{
  const pages=[];
  const result=readRepositoryTestVariables({get:path=>{
    const page=Number(path.split('=').at(-1));pages.push(page);
    return {total_count:60,variables:page<=2?Array.from({length:30},(_,i)=>variable(`NAME_${(page-1)*30+i}`)):[]};
  }},repository);
  assert.deepEqual(pages,[1,2,3]);assert.deepEqual(result,{});
});
test('a pin setting after page one is not lost',()=>{
  const result=readRepositoryTestVariables({get:path=>({total_count:31,variables:path.endsWith('=1')?Array.from({length:30},(_,i)=>variable(`NAME_${i}`)):[variable('WSS_USE_PIN_TESTS','false')]})},repository);
  assert.deepEqual(result,{WSS_USE_PIN_TESTS:'false'});
});
test('permission failures are not unset settings',()=>{
  for(const status of [401,403,404,500])assert.throws(()=>readRepositoryTestVariables({get:()=>{throw Error(`HTTP ${status}`);}},repository),new RegExp(String(status)));
});
test('malformed, duplicate, incomplete and changed pages fail closed',()=>{
  for(const response of [{total_count:'1',variables:[]},{total_count:2,variables:[variable('ONE')]},{total_count:1,variables:[variable('ONE'),variable('ONE')]},{total_count:1,variables:[variable('BAD-NAME')]},{total_count:1,variables:[variable('ONE',true)]},{total_count:0,variables:[variable('ONE')]}])assert.throws(()=>readRepositoryTestVariables({get:()=>response},repository));
  assert.throws(()=>readRepositoryTestVariables({get:path=>path.endsWith('=1')?{total_count:31,variables:Array.from({length:30},(_,i)=>variable(`NAME_${i}`))}:{total_count:30,variables:[]}},repository),/count changed/);
});
test('snapshot map uses each target own repository and unique identity',()=>{
  const targets=[{id:'python-wrapper',repository},{id:'python-cli',repository:'cwru-non-academic/CLI_Python_WSS_Application'}],paths=[];
  assert.deepEqual(readTestVariableSnapshots({get:path=>{paths.push(path);return {total_count:0,variables:[]};}},{targets}),{'python-wrapper':{},'python-cli':{}});
  assert.equal(paths.length,2);assert.ok(paths[1].includes(targets[1].repository));
  assert.throws(()=>readTestVariableSnapshots({get:()=>({total_count:0,variables:[]})},{targets:[targets[0],targets[0]]}),/duplicate/);
});
