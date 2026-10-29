import {test} from 'node:test';
import assert from 'node:assert/strict';
import {fileEquivalence,sourceEquivalence} from './equivalence.mjs';

function fixture() {
  const p={schema:'wss-noop-projection/v1',archive_inventory:['Core.dll','backends/macos/Mac.dll','backends/macos/native.dylib','net462/unused.dll'],files:[
    {candidate_path:'Core.dll',installed_path:'Core.dll'},{candidate_path:'backends/macos/Mac.dll',installed_path:'mac/Mac.dll'},{candidate_path:'backends/macos/native.dylib',installed_path:'mac/native.dylib'}],preserved_metadata:['wss-candidate.json']};
  const c=Object.fromEntries(p.archive_inventory.map(x=>[x,Buffer.from(x)]));
  const installed=Object.fromEntries(p.files.map(x=>[x.installed_path,c[x.candidate_path]]));installed['wss-candidate.json']=Buffer.from('original valid provenance');
  const anchor={...installed};return {p,c,installed,anchor};
}
test('equivalence keeps a reviewed projection and preserves original provenance bytes',()=>{
  const f=fixture(),proof=fileEquivalence(f.p,f.c,f.installed,f.anchor);
  assert.equal(proof.files.length,3);assert.equal(proof.preserved_metadata.length,1);
});
for(const[name,mutate]of[
  ['extra archive file',f=>f.c['extra.dll']=Buffer.from('extra')],['missing native archive file',f=>delete f.c['backends/macos/native.dylib']],
  ['extra installed file',f=>f.installed['extra.dll']=Buffer.from('extra')],['missing installed Mac pair',f=>delete f.installed['mac/Mac.dll']],
  ['different candidate bytes',f=>f.c['Core.dll']=Buffer.from('new code')],['different installed bytes',f=>f.installed['Core.dll']=Buffer.from('corrupt')],
  ['missing old anchor backend',f=>delete f.anchor['mac/native.dylib']],['fabricated provenance rewrite',f=>f.installed['wss-candidate.json']=Buffer.from('new candidate metadata')],
  ['case-alias archive file',f=>f.c['CORE.dll']=Buffer.from('alias')],['projection path traversal',f=>f.p.files[0].installed_path='../Core.dll'],
  ['projection duplicate destination',f=>f.p.files[1].installed_path='Core.dll'],['projection duplicate source',f=>f.p.files[1].candidate_path='Core.dll'],
  ['unreviewed projection source',f=>f.p.files[0].candidate_path='other.dll'],['hash-only bytes claim',f=>f.c['Core.dll']='a'.repeat(64)]
])test(`no-op rejects ${name}`,()=>{const f=fixture();mutate(f);assert.throws(()=>fileEquivalence(f.p,f.c,f.installed,f.anchor));});
test('source equivalence tolerates only explicitly reviewed metadata changes',()=>{
  const before={'src/runtime.cs':'a'.repeat(64),'tests/canonical.cs':'b'.repeat(64),'README.md':'c'.repeat(64),'.github/workflows/ci.yml':'d'.repeat(64)};
  const after={...before,'README.md':'e'.repeat(64),'.github/workflows/ci.yml':'f'.repeat(64)};
  assert.equal(sourceEquivalence(before,after,['README.md','.github/']).files,2);
  after['src/runtime.cs']='c'.repeat(64);assert.throws(()=>sourceEquivalence(before,after,['README.md','.github/']));
});
test('changed or removed canonical tests cannot hide behind a no-op',()=>{
  const before={'tests/canonical.cs':'a'.repeat(64)};
  assert.throws(()=>sourceEquivalence(before,{},['README.md','.github/']));
  assert.throws(()=>sourceEquivalence(before,{'tests/canonical.cs':'b'.repeat(64)},['README.md','.github/']));
});
test('unsafe broad exclusions are not an equivalence policy',()=>{
  for(const paths of [[''],['/'],['../'],['.\\'],['src/'],['tests/'],['.github/','.github/']])assert.throws(()=>sourceEquivalence({}, {}, paths));
});
test('empty or metadata-only source inventories cannot manufacture equivalence',()=>{
  assert.throws(()=>sourceEquivalence({}, {}, []));
  assert.throws(()=>sourceEquivalence({'README.md':'a'.repeat(64)},{'README.md':'b'.repeat(64)},['README.md']));
});
