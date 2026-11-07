// Inventory only: no repository content, variables, settings, or grant changes.
import {readFileSync} from 'node:fs';
import {isEntrypoint} from '../release/entrypoint.mjs';
const owner='cwru-non-academic';
const expectedId='160160114';
const expectedSlug='cwru-wss-release-automation';
const targetNames=[
  'CSharp_WSS_Integration_Lib','CLI_CSharp_WSS_Application',
  'Unity_WSS_Integration_Lib','GUI_Unity_WSS_Application',
  'Python_WSS_Integration_Lib','CLI_Python_WSS_Application','ROS_Python_WSS_Application',
  'Matlab_WSS_Integration_Lib','CLI_Matlab_WSS_Application',
];
const fail=()=>{throw Error('Installation scope verification failed; no private response was published');};
function requireValid(value){if(!value)fail();}
async function snapshot(get){
  const rows=[];const ids=new Set();const names=new Set();let total;
  // The bound prevents an unbounded response or changing remote inventory.
  for(let page=1;page<=10000;page++){
    let response;
    try{response=await get(`installation/repositories?per_page=100&page=${page}`);}catch{fail();}
    requireValid(Number.isSafeInteger(response?.total_count)&&response.total_count>=0&&response.total_count<=1000000&&Array.isArray(response.repositories));
    if(total===undefined)total=response.total_count;
    requireValid(response.total_count===total);
    const expectedLength=Math.min(100,total-rows.length);
    requireValid(response.repositories.length===expectedLength);
    for(const repo of response.repositories){
      requireValid(Number.isSafeInteger(repo?.id)&&repo.id>0&&typeof repo.full_name==='string'&&new RegExp(`^${owner}/[A-Za-z0-9_.-]+$`).test(repo.full_name));
      requireValid(['public','private','internal'].includes(repo.visibility)&&typeof repo.private==='boolean'&&(repo.visibility==='public'?repo.private===false:repo.private===true));
      const key=repo.full_name.toLowerCase();
      requireValid(!ids.has(repo.id)&&!names.has(key));ids.add(repo.id);names.add(key);
      rows.push({id:repo.id,name:repo.full_name,visibility:repo.visibility});
    }
    if(rows.length===total)return rows.sort((a,b)=>a.id-b.id);
  }
  fail();
}
export async function inspectInstallation({get,installationId,appSlug,policy}){
  requireValid(installationId===expectedId&&appSlug===expectedSlug);
  requireValid(policy?.schema==='wss-campaign-test-selection/v1'&&Array.isArray(policy.targets)&&JSON.stringify(policy.targets.map(t=>t.repository))===JSON.stringify(targetNames.map(n=>`${owner}/${n}`)));
  const first=await snapshot(get);const second=await snapshot(get);
  requireValid(JSON.stringify(first)===JSON.stringify(second));
  const names=new Set(first.map(r=>r.name));
  return {
    schema:'wss-installation-scope/v1',installation_id:expectedId,app_slug:expectedSlug,
    repository_selection:'unknown',total_count:first.length,
    public_repositories:first.filter(r=>r.visibility==='public').map(r=>r.name).sort(),
    nonpublic_count:first.filter(r=>r.visibility!=='public').length,
    known_targets:targetNames.map(n=>({repository:`${owner}/${n}`,present:names.has(`${owner}/${n}`)})),
  };
}
export function metadataClient(token,fetchImpl=fetch){
  requireValid(typeof token==='string'&&token.length>0);
  return async path=>{
    requireValid(/^installation\/repositories\?per_page=100&page=[1-9][0-9]*$/.test(path));
    try{
      const response=await fetchImpl(`https://api.github.com/${path}`,{
        method:'GET',redirect:'error',signal:AbortSignal.timeout(30000),
        headers:{Authorization:`Bearer ${token}`,Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28'},
      });
      requireValid(response.ok);return await response.json();
    }catch{fail();}
  };
}
if(isEntrypoint(import.meta.url)){
  try{
    requireValid(process.argv.length===2);
    const policy=JSON.parse(readFileSync(new URL('./test-source-targets.json',import.meta.url),'utf8'));
    const result=await inspectInstallation({get:metadataClient(process.env.GH_TOKEN),installationId:process.env.WSS_INSTALLATION_ID,appSlug:process.env.WSS_APP_SLUG,policy});
    console.log(JSON.stringify(result,null,2));
  }catch{console.error('Installation scope verification failed; no private response was published');process.exitCode=1;}
}
