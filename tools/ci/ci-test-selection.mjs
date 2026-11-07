// Ordinary CI snapshots repository settings once. A current, selection-enabled
// product campaign must reuse its authenticated lock instead of resolving latest.
import {resolveTestSelection} from './test-selection.mjs';
import {loadCampaignTestBinding} from './selected-test-source.mjs';
import {candidate,canonical,CORE,requireThat} from '../release/verify.mjs';
import {channelContext} from '../release/channels.mjs';
const sha=v=>typeof v==='string'&&/^[a-f0-9]{40}$/.test(v);
export async function selectCiTests({channel,targetId,policy,variables={},run,productCandidate=null,mode='committed'},g,{loadCampaign=loadCampaignTestBinding}={}) {
  const context=channelContext(channel);
  requireThat(['committed','candidate'].includes(mode)&&policy&&Array.isArray(policy.targets),'Invalid CI test selection mode/policy');
  const matches=policy.targets.filter(t=>t.id===targetId);
  requireThat(matches.length===1,'Missing or duplicate test target policy');
  const target=matches[0];
  requireThat(run&&run.repository===target.repository&&sha(run.commit)&&typeof run.run_id==='string'&&/^[1-9][0-9]*$/.test(run.run_id)&&['pull_request','push','workflow_dispatch'].includes(run.event),'CI test selection must bind the actual target run/source');
  if(mode==='candidate')requireThat(productCandidate&&run.event==='workflow_dispatch','Candidate tests require the explicit canonical producer and dispatch');
  if(productCandidate)candidate(productCandidate);
  // PR checks validate proposed product source independently of an old installed
  // payload's campaign. Actual candidate and committed current-producer runs use
  // the frozen campaign selection whenever the producer policy requires it.
  if(productCandidate&&run.event!=='pull_request'&&(mode==='candidate'||g.head(CORE,channel)===productCandidate.commit)) {
    const file=g.get(`repos/${CORE}/contents/${context.targetPolicy}?ref=${productCandidate.commit}`);
    requireThat(file?.type==='file'&&file.encoding==='base64'&&typeof file.content==='string','Could not read exact producer test policy');
    const manifest=JSON.parse(Buffer.from(file.content,'base64').toString('utf8'));
    requireThat(manifest.channel===channel,'Producer test policy channel mismatch');
    if(Object.hasOwn(manifest,'test_selection_policy')) {
      const binding=await loadCampaign(g,{kind:'acceptance',runId:productCandidate.run_id,channel,targetId,expectedCandidate:productCandidate});
      requireThat(binding&&binding.selection.targets[targetId].repository===run.repository,'Campaign selected another target');
      return {scope:'campaign',...binding};
    }
  }
  const request={channel,campaign:{repository:run.repository,commit:run.commit,run_id:run.run_id},targets:[{...target,variables}]};
  const selection=resolveTestSelection(request,g);
  return {scope:'run',selection,request,selection_id:selection.selection_id};
}
