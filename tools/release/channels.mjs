// Branch/release identity shared by Core and all receiver adapters. Publication
// permission remains a separate, explicit gate; channel selection cannot grant it.
import {existsSync,realpathSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
export function channelContext(value) {
  if(!['dev','main'].includes(value))throw Error('CI channel must be exactly dev or main');
  const stable=value==='main';
  return Object.freeze({channel:value,branch:value,ref:`refs/heads/${value}`,kind:stable?'stable':'beta',
    acceptanceVariable:'WSS_DEV_ACCEPTANCE_ENABLED',publicationVariable:'WSS_DEV_RELEASE_ENABLED',
    manifestSchema:stable?'wss-required-targets/v2':'wss-required-targets/v1',
    planSchema:stable?'wss-propagation-plan/v2':'wss-propagation-plan/v1',
    receiptSchema:stable?'wss-acceptance-receipt/v2':'wss-acceptance-receipt/v1',
    acceptanceSchema:stable?'wss-release-acceptance/v2':'wss-release-acceptance/v1',
    stageSchema:stable?'wss-main-stage/v1':'wss-dev-stage/v1',
    stagePlanSchema:stable?'wss-main-propagation-plan/v1':'wss-dev-propagation-plan/v1',
    receiverSchema:stable?'wss-main-stage-receiver/v1':'wss-dev-stage-receiver/v1',
    receiverPolicy:`.github/${value}-propagation.json`,
    targetPolicy:`tools/release/targets.${value}.json`,stagePolicy:`tools/ci/${value}-propagation.json`,
    stageWorkflow:`.github/workflows/${value}-propagation.yml`,acceptanceWorkflow:`.github/workflows/${value}-release.yml`,
    stageArtifact:stable?'wss-main-propagation-plan':'wss-dev-propagation-plan'});
}
export function targetForChannel(target,value) {const context=channelContext(value);return {...target,branch:context.branch};}
export function integrationConfiguration(family) {
  const paths={csharp:'.github/universal-bootstrap.json',unity:'.github/unity-ci.json',python:'.github/python-ci.json',matlab:'.github/matlab-ci.json'};
  if(!Object.hasOwn(paths,family))throw Error('Unknown integration configuration role');
  return paths[family];
}
export function channelActivation(value) {
  if(value?.schema!=='wss-ci-channel-activation/v1'||value.dev!==true||typeof value.main!=='boolean'||Object.keys(value).sort().join(',')!=='dev,main,schema')throw Error('Invalid Core channel activation configuration');
  return Object.freeze({...value});
}
export function controllerContext(env,operation,coreRepository,activation={main:false}) {
  const context=channelContext(env.WSS_CI_CHANNEL||'dev');
  if(!['prepare-dispatches','dispatch','verify','publish','stage-prepare','stage-dispatch'].includes(operation))throw Error('Unknown CI controller operation');
  const expected=`${coreRepository}/${operation.startsWith('stage-')?context.stageWorkflow:context.acceptanceWorkflow}@refs/heads/main`;
  if(env.GITHUB_WORKFLOW_REF&&env.GITHUB_WORKFLOW_REF!==expected)throw Error('Acceptance controller belongs to another channel');
  if(context.channel==='main') {
    if(env.WSS_CI_CHANNEL!=='main'||env.GITHUB_ACTIONS!=='true'||env.GITHUB_REPOSITORY!==coreRepository||env.GITHUB_REF!=='refs/heads/main'||env.GITHUB_WORKFLOW_REF!==expected||
      !/^[a-f0-9]{40}$/.test(env.GITHUB_SHA||'')||env.GITHUB_WORKFLOW_SHA!==env.GITHUB_SHA||!['workflow_run','schedule','workflow_dispatch'].includes(env.GITHUB_EVENT_NAME))throw Error('Main acceptance requires its exact registered trusted-main controller');
    if(!['verify','stage-prepare'].includes(operation)&&activation.main!==true)throw Error('Main CI mutation is not activated');
  }
  // Explicit temporary alias approved for both channels. Neither channel
  // selection nor stable_enabled grants permission to mutate or publish.
  if(['prepare-dispatches','dispatch','publish'].includes(operation)&&env[context.acceptanceVariable]!=='true')throw Error('Acceptance reconciliation is not enabled');
  if(operation==='publish'&&env[context.publicationVariable]!=='true')throw Error('Publication must be explicitly enabled after reviewed deployment');
  return context;
}
export function releaseFingerprint(value,run,planId) {
  const c=channelContext(value);
  if(!/^[1-9][0-9]*$/.test(run)||!/^[a-f0-9]{64}$/.test(planId))throw Error('Fingerprint requires exact producer and frozen plan identities');
  return `${c.channel==='dev'?'dev-beta':'main-stable'}-${run}-${planId.slice(0,12)}`;
}
// macOS temporary paths can name /var while Node canonicalizes the loaded
// module to /private/var. Imported assertion sentinels are not filesystem paths.
if(process.argv[1]&&existsSync(process.argv[1])&&realpathSync(process.argv[1])===fileURLToPath(import.meta.url))console.log(JSON.stringify(channelContext(process.argv[2])));
