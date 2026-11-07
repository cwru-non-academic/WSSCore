// Read-only capability probe. This does not resolve tests, create campaign proof,
// expand App grants, or change any repository setting.
import {readFileSync,appendFileSync} from 'node:fs';
import {readTestVariableSnapshots} from './repository-test-variables.mjs';
import {GitHub} from '../release/github.mjs';
import {canonical,requireThat} from '../release/verify.mjs';
import {isEntrypoint} from '../release/entrypoint.mjs';
const expected=[
  ['csharp-wrapper','CSharp_WSS_Integration_Lib'],['csharp-cli','CLI_CSharp_WSS_Application'],
  ['unity-wrapper','Unity_WSS_Integration_Lib'],['unity-gui','GUI_Unity_WSS_Application'],
  ['python-wrapper','Python_WSS_Integration_Lib'],['python-cli','CLI_Python_WSS_Application'],
  ['python-ros','ROS_Python_WSS_Application'],['matlab-wrapper','Matlab_WSS_Integration_Lib'],
  ['matlab-cli','CLI_Matlab_WSS_Application'],
];
export function readinessTargets(policy) {
  requireThat(policy?.schema==='wss-campaign-test-selection/v1' && Array.isArray(policy.targets),'Invalid reviewed readiness targets');
  requireThat(canonical(policy.targets.map(t=>[t.id,t.repository]))===canonical(expected.map(([id,repo])=>[id,`cwru-non-academic/${repo}`])),'Readiness must cover exactly the nine reviewed repositories');
  return policy.targets.map(({id,repository})=>({id,repository}));
}
export function checkVariableReadiness(g,policy) {
  const targets=readinessTargets(policy);
  const allowed=new Set(targets.map(t=>t.repository));
  const readOnly={get(path){
    const match=/^repos\/([^/]+\/[^/]+)\/actions\/variables\?per_page=30&page=([1-9][0-9]*)$/.exec(path);
    requireThat(match && allowed.has(match[1]),'Readiness permits only reviewed repository variable GETs');
    try { return g.get(path); }
    catch { throw Error(`Repository Variables read failed: ${match[1]}; no grant or setting was changed`); }
  }};
  // Discard every variable value, including the selected settings. Successful
  // empty responses establish capability but not any particular pin policy.
  readTestVariableSnapshots(readOnly,{targets});
  return {schema:'wss-test-variable-readiness/v1',capability:'repository-variables-read',targets:targets.map(t=>({...t,read:'verified'}))};
}
if(isEntrypoint(import.meta.url)) {
  try {
    const [operation,...extra]=process.argv.slice(2);
    requireThat(extra.length===0 && ['repositories','check'].includes(operation),'Usage: test-variable-readiness.mjs repositories|check');
    const policy=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url),'utf8'));
    if(operation==='repositories') {
      requireThat(process.env.GITHUB_OUTPUT,'GitHub output path is required');
      appendFileSync(process.env.GITHUB_OUTPUT,`repositories=${readinessTargets(policy).map(t=>t.repository.split('/')[1]).join(',')}\n`);
    } else {
      requireThat(process.env.GH_TOKEN,'Existing scoped App token is required');
      console.log(JSON.stringify(checkVariableReadiness(new GitHub(),policy),null,2));
    }
  } catch(error) { console.error(error.message); process.exitCode=1; }
}
