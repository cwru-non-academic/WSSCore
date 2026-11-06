import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {mkdtempSync,mkdirSync,writeFileSync,symlinkSync,rmSync} from 'node:fs';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
import {pathToFileURL} from 'node:url';
import {isEntrypoint} from './entrypoint.mjs';
test('actual CLI paths tolerate directory aliases while eval imports never enter the CLI',()=>{
  const root=mkdtempSync(join(tmpdir(),'wss entrypoint ')),physical=join(root,'physical source'),alias=join(root,'directory alias');
  try{
    mkdirSync(physical);symlinkSync(physical,alias,process.platform==='win32'?'junction':'dir');
    const file=join(physical,'entry.mjs');
    writeFileSync(file,`import {isEntrypoint} from ${JSON.stringify(new URL('./entrypoint.mjs',import.meta.url).href)};if(isEntrypoint(import.meta.url))console.log('actual CLI');\n`);
    assert.equal(execFileSync(process.execPath,[join(alias,'entry.mjs')],{encoding:'utf8'}).trim(),'actual CLI');
    const sentinel=join(alias,'entry.mjs');
    assert.equal(execFileSync(process.execPath,['--input-type=module','--eval',`await import(${JSON.stringify(pathToFileURL(file).href)})`,'--',sentinel],{encoding:'utf8'}),'');
    for(const args of [['--eval'],['-e'],['--eval=code'],['--print'],['-p'],['--print=code']])assert.equal(isEntrypoint(pathToFileURL(file),['node',sentinel],args),false);
  }finally{rmSync(root,{recursive:true,force:true});}
});
