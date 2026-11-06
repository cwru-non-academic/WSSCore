// Core-only control configuration. Receivers share channel identity but do not
// need a source/lock rollout when Core's publication variable mapping changes.
import {readFileSync} from 'node:fs';
import {channelContext} from '../release/channels.mjs';
export function channelActivation(value) {
  if(value?.schema!=='wss-ci-channel-activation/v1'||value.dev!==true||typeof value.main!=='boolean'||Object.keys(value).sort().join(',')!=='dev,main,schema')throw Error('Invalid Core channel activation configuration');
  return Object.freeze({...value});
}
export function publicationControls(value=JSON.parse(readFileSync(new URL('./publication-controls.json',import.meta.url)))) {
  if(value?.schema!=='wss-ci-publication-controls/v1'||value.acceptance!=='WSS_DEV_ACCEPTANCE_ENABLED'||value.dev!=='WSS_DEV_RELEASE_ENABLED'||
    !['WSS_DEV_RELEASE_ENABLED','WSS_MAIN_RELEASE_ENABLED'].includes(value.main)||Object.keys(value).sort().join(',')!=='acceptance,dev,main,schema')throw Error('Invalid Core publication controls');
  return Object.freeze({...value});
}
export function controllerContext(env,operation,coreRepository,activation={main:false},controls=publicationControls()) {
  const base=channelContext(env.WSS_CI_CHANNEL||'dev'),mapping=publicationControls(controls);
  const context=Object.freeze({...base,acceptanceVariable:mapping.acceptance,publicationVariable:mapping[base.channel]});
  if(!['prepare-dispatches','dispatch','verify','publish','stage-prepare','stage-dispatch'].includes(operation))throw Error('Unknown CI controller operation');
  const expected=`${coreRepository}/${operation.startsWith('stage-')?context.stageWorkflow:context.acceptanceWorkflow}@refs/heads/main`;
  if(env.GITHUB_WORKFLOW_REF&&env.GITHUB_WORKFLOW_REF!==expected)throw Error('Acceptance controller belongs to another channel');
  if(context.channel==='main') {
    if(env.WSS_CI_CHANNEL!=='main'||env.GITHUB_ACTIONS!=='true'||env.GITHUB_REPOSITORY!==coreRepository||env.GITHUB_REF!=='refs/heads/main'||env.GITHUB_WORKFLOW_REF!==expected||
      !/^[a-f0-9]{40}$/.test(env.GITHUB_SHA||'')||env.GITHUB_WORKFLOW_SHA!==env.GITHUB_SHA||!['workflow_run','schedule','workflow_dispatch'].includes(env.GITHUB_EVENT_NAME))throw Error('Main acceptance requires its exact registered trusted-main controller');
    if(!['verify','stage-prepare'].includes(operation)&&activation.main!==true)throw Error('Main CI mutation is not activated');
  }
  if(['prepare-dispatches','dispatch','publish'].includes(operation)&&env[context.acceptanceVariable]!=='true')throw Error('Acceptance reconciliation is not enabled');
  if(operation==='publish'&&env[context.publicationVariable]!=='true')throw Error('Publication must be explicitly enabled after reviewed deployment');
  return context;
}
