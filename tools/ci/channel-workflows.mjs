// Canonical controller definitions. Channel overlays cannot select code,
// permissions, credentials or alternative publication implementations.
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {channelContext} from '../release/channels.mjs';
import {channelActivation,publicationControls} from './channel-controls.mjs';
import {isEntrypoint} from '../release/entrypoint.mjs';
import {INITIAL_HANDOFF_PROFILES,initialHandoffProfile} from './dev-propagation.mjs';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
export function renderInitialWorkflow(family,{activation=channelActivation(JSON.parse(readFileSync(resolve(root,'tools/ci/channel-activation.json'),'utf8')))}={}) {
  const profile=initialHandoffProfile(family),config=channelActivation(activation);
  const branches=['dev','main'].filter(channel=>config[channel]);
  const values={FAMILY:family,LABEL:{python:'Python',unity:'Unity',matlab:'MATLAB'}[family],
    REPOSITORY:profile.repository.split('/')[1],BRANCHES:branches.join(', '),
    ENABLED_BRANCHES:branches.map(channel=>`github.event.workflow_run.head_branch == '${channel}'`).join(' || ')};
  const text=readFileSync(resolve(root,'tools/ci/templates/initial-handoff.yml'),'utf8').replace(/\r\n/g,'\n').replace(/__WSS_([A-Z_]+)__/g,(_,key)=>{
    if(!Object.hasOwn(values,key))throw Error('Unknown initial template field');return values[key];
  });
  if(/__WSS_/.test(text))throw Error('Unresolved initial template field');
  return {path:`.github/workflows/propagate-${family}.yml`,text};
}
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
  const rendered=[...['dev','main'].flatMap(channel=>['propagation','acceptance'].map(operation=>renderChannelWorkflow(operation,channel))),
    ...Object.keys(INITIAL_HANDOFF_PROFILES).map(family=>renderInitialWorkflow(family))];
  for(const result of rendered){
    const path=resolve(root,result.path);
    if(command==='write'){mkdirSync(dirname(path),{recursive:true});writeFileSync(path,result.text);}
    else if(readFileSync(path,'utf8').replace(/\r\n/g,'\n')!==result.text)throw Error(`Channel workflow drift: ${result.path}`);
  }
  console.log(`Channel workflows ${command}: one implementation per operation; exact closed overlays.`);
}
if(isEntrypoint(import.meta.url))main();
