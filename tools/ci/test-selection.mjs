// Test-source selection is separate from product, workflow and release-tool identity.
import {readFileSync,writeFileSync,mkdtempSync,linkSync,rmSync} from 'node:fs';
import {dirname,join} from 'node:path';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {GitHub} from '../release/github.mjs';
import {canonical,requireThat} from '../release/verify.mjs';
import {resolveTestSelection,validateTestSelection} from '../release/test-selection-contract.mjs';
export {TEST_VARIABLES,resolveTestSelection,verifyTestSelection,verifySelectedTestEvidence} from '../release/test-selection-contract.mjs';

// Create-only output prevents a rerun from silently replacing a campaign's lock.
// Reuse does not read current variables or resolve branch tips again. The caller
// must still authenticate a downloaded lock against its frozen plan before use.
export function resolveOrReuseTestSelection(input,g,path) {
  const read=()=>validateTestSelection(JSON.parse(readFileSync(path,'utf8')),input);
  try { return read(); } catch(error) { if(error.code!=='ENOENT')throw error; }
  const selected=resolveTestSelection(input,g);
  // Publish complete bytes atomically without replacing another creator's lock.
  // The temporary file is on the same volume as the final path.
  const temporary=mkdtempSync(join(dirname(path),'.wss-test-selection-'));
  try {
    const complete=join(temporary,'selection.json');
    writeFileSync(complete,canonical(selected)+'\n',{encoding:'utf8',flag:'wx'});
    try { linkSync(complete,path); }
    catch(error) { if(error.code!=='EEXIST')throw error; return read(); }
    return selected;
  } finally { rmSync(temporary,{recursive:true,force:true}); }
}

if(isEntrypoint(import.meta.url)) {
  const [operation,inputPath,outputPath,...rest]=process.argv.slice(2);
  requireThat(operation==='resolve' && inputPath && outputPath && rest.length===0,'Usage: test-selection.mjs resolve <reviewed-request.json> <selection.json>');
  const selection=resolveOrReuseTestSelection(JSON.parse(readFileSync(inputPath,'utf8')),new GitHub(),outputPath);
  console.log(JSON.stringify({selection_id:selection.selection_id,targets:selection.targets}));
}
