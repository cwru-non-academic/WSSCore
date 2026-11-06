// Canonical controller definitions. Channel overlays cannot select code,
// permissions, credentials or alternative publication implementations.
import {readFileSync,writeFileSync,mkdirSync,existsSync,realpathSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {channelContext} from '../release/channels.mjs';
import {channelActivation,publicationControls} from './channel-controls.mjs';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
export function renderChannelWorkflow(operation,channel,{activation=channelActivation(JSON.parse(readFileSync(resolve(root,'tools/ci/channel-activation.json'),'utf8'))),controls=publicationControls()}={}){
  const c=channelContext(channel),config=channelActivation(activation);
  if(!['propagation','acceptance'].includes(operation))throw Error('Unknown channel workflow operation');
  let template=readFileSync(resolve(root,`tools/ci/templates/${operation}.yml`),'utf8').replace(/\r\n/g,'\n');
  if(!config[channel])template=template.replace(/^  workflow_run:\n[\s\S]*?(?=^  workflow_dispatch:)/m,'');
  const values={CHANNEL:channel,KIND:c.kind,PUBLICATION_VARIABLE:publicationControls(controls)[channel],STAGE_ENGINE:'tools/ci/dev-propagation.mjs',STAGE_MINUTE:channel==='dev'?'37':'47',ACCEPTANCE_MINUTE:channel==='dev'?'17':'27',
    ENABLED:String(config[channel]),MODE_DEFAULT:channel==='dev'?'reconcile':'verify',RECONCILE:config[channel]?"(github.event_name != 'workflow_dispatch' || inputs.mode == 'reconcile')":'false'};
  const text=template.replace(/__WSS_([A-Z_]+)__/g,(_,key)=>{if(!Object.hasOwn(values,key))throw Error('Unknown channel template field');return values[key];});
  if(/__WSS_/.test(text))throw Error('Unresolved channel template field');
  return {path:operation==='propagation'?c.stageWorkflow:c.acceptanceWorkflow,text};
}
function main(){
  const command=process.argv[2];if(!['write','check'].includes(command)||process.argv.length!==3)throw Error('Usage: channel-workflows.mjs write|check');
  for(const channel of ['dev','main'])for(const operation of ['propagation','acceptance']){
    const result=renderChannelWorkflow(operation,channel),path=resolve(root,result.path);
    if(command==='write'){mkdirSync(dirname(path),{recursive:true});writeFileSync(path,result.text);}
    else if(readFileSync(path,'utf8').replace(/\r\n/g,'\n')!==result.text)throw Error(`Channel workflow drift: ${result.path}`);
  }
  console.log(`Channel workflows ${command}: one implementation per operation; exact closed overlays.`);
}
if(process.argv[1]&&existsSync(process.argv[1])&&realpathSync(process.argv[1])===fileURLToPath(import.meta.url))main();
