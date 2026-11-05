// Branch/release identity shared by Core and all receiver adapters. Publication
// permission remains a separate, explicit gate; channel selection cannot grant it.
import {resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
export function channelContext(value) {
  if(!['dev','main'].includes(value))throw Error('CI channel must be exactly dev or main');
  const stable=value==='main';
  return Object.freeze({channel:value,branch:value,ref:`refs/heads/${value}`,kind:stable?'stable':'beta',
    manifestSchema:stable?'wss-required-targets/v2':'wss-required-targets/v1',
    planSchema:stable?'wss-propagation-plan/v2':'wss-propagation-plan/v1',
    receiptSchema:stable?'wss-acceptance-receipt/v2':'wss-acceptance-receipt/v1',
    acceptanceSchema:stable?'wss-release-acceptance/v2':'wss-release-acceptance/v1',
    targetPolicy:`tools/release/targets.${value}.json`,stagePolicy:`tools/ci/${value}-propagation.json`,
    stageWorkflow:`.github/workflows/${value}-propagation.yml`,acceptanceWorkflow:`.github/workflows/${value}-release.yml`,
    stageArtifact:stable?'wss-main-propagation-plan':'wss-dev-propagation-plan'});
}
export function targetForChannel(target,value) {const context=channelContext(value);return {...target,branch:context.branch};}
export function releaseFingerprint(value,run,planId) {
  const c=channelContext(value);
  if(!/^[1-9][0-9]*$/.test(run)||!/^[a-f0-9]{64}$/.test(planId))throw Error('Fingerprint requires exact producer and frozen plan identities');
  return `${c.channel==='dev'?'dev-beta':'main-stable'}-${run}-${planId.slice(0,12)}`;
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url))console.log(JSON.stringify(channelContext(process.argv[2])));
