import {canonical,CORE,requireThat,candidate} from './verify.mjs';
import {channelContext} from './channels.mjs';
import {testSelectionPolicy,selectionRequest} from './campaign-test-selection.mjs';
import {resolveTestSelection,verifyTestSelection} from './test-selection-contract.mjs';

export const TEST_SELECTION_ARTIFACT='wss-test-selection';
const WORKFLOW='.github/workflows/artifacts.yml';
const same=(a,b)=>canonical(a)===canonical(b);
const decimal=x=>typeof x==='string'&&/^[1-9][0-9]*$/.test(x);
function authority(g,m,policy,c,env) {
  candidate(c);
  const context=channelContext(m.channel),attempt=Number(env.GITHUB_RUN_ATTEMPT);
  requireThat(policy.channel===m.channel&&testSelectionPolicy(m)&&same(testSelectionPolicy(m),testSelectionPolicy(policy)),'Producer requires identical reviewed test policies');
  requireThat(env.GITHUB_ACTIONS==='true'&&env.GITHUB_REPOSITORY===CORE&&env.GITHUB_EVENT_NAME==='push'&&env.GITHUB_REF===context.ref&&env.GITHUB_SHA===c.commit&&env.GITHUB_WORKFLOW_SHA===c.commit&&env.GITHUB_WORKFLOW_REF===`${CORE}/${WORKFLOW}@${context.ref}`&&env.GITHUB_RUN_ID===c.run_id&&decimal(env.GITHUB_RUN_ATTEMPT)&&Number.isSafeInteger(attempt),'Test selection requires the exact canonical producer runner');
  const r=g.run(CORE,c.run_id);
  requireThat(String(r.id)===c.run_id&&r.repository?.full_name===CORE&&r.head_repository?.full_name===CORE&&r.path===WORKFLOW&&r.head_sha===c.commit&&r.head_branch===m.channel&&r.event==='push'&&r.status==='in_progress'&&r.run_attempt===attempt,'Test selection producer run/source/attempt mismatch');
  requireThat(g.head(CORE,m.channel)===c.commit,'Core moved before producer test selection');
  for(const [path,expected] of [[context.targetPolicy,m],[context.stagePolicy,policy]]) {
    const file=g.get(`repos/${CORE}/contents/${path}?ref=${c.commit}`);
    requireThat(file?.type==='file'&&file.encoding==='base64'&&same(JSON.parse(Buffer.from(file.content,'base64').toString('utf8')),expected),'Test selection policy differs from immutable producer policy');
  }
  return attempt;
}
function existing(g,m,c,attempt) {
  const artifacts=g.pages(`repos/${CORE}/actions/runs/${c.run_id}/artifacts`,'artifacts').filter(a=>a.name===TEST_SELECTION_ARTIFACT);
  requireThat(artifacts.length<=1,'Ambiguous producer test selection artifact');
  if(!artifacts.length)return null;
  const a=g.artifact(CORE,c.run_id,TEST_SELECTION_ARTIFACT);
  requireThat(String(a.id)===String(artifacts[0].id)&&/^[1-9][0-9]*$/.test(String(a.id))&&a.name===TEST_SELECTION_ARTIFACT&&a.expired===false&&artifacts[0].expired===false&&String(a.workflow_run?.id)===c.run_id&&a.workflow_run?.head_sha===c.commit,'Producer test selection artifact identity/expiry mismatch');
  const bytes=g.download(CORE,c.run_id,TEST_SELECTION_ARTIFACT,['selection.json'])['selection.json'];
  const document=JSON.parse(bytes.toString('utf8'));
  requireThat(same(Object.keys(document).sort(),['producer','schema','selection'])&&document.schema==='wss-test-selection-artifact/v1'&&same(document.producer,{repository:CORE,commit:c.commit,run_id:c.run_id,attempt:1,workflow:WORKFLOW})&&document.producer.attempt<=attempt,'Producer test selection artifact envelope/creating attempt mismatch');
  verifyTestSelection(document.selection,selectionRequest(m,c),document.selection.selection_id);
  return {created:false,selection:document.selection,document};
}
// Only absence on the first attempt permits a new resolution. API failures,
// expiry, corruption and ambiguity are never absence. Upload document as the
// sole selection.json file before allowing either freeze or initial dispatch.
export function prepareProducerTestSelection(g,m,policy,c,{resolveSnapshots,env=process.env}={}) {
  const attempt=authority(g,m,policy,c,env),saved=existing(g,m,c,attempt);
  if(saved)return saved;
  requireThat(attempt===1,'Previous producer attempt has no durable test selection; inspect recovery');
  requireThat(typeof resolveSnapshots==='function','Authenticated repository variable snapshot reader required');
  const selection=resolveTestSelection(selectionRequest(m,c,resolveSnapshots(testSelectionPolicy(m).targets)),g);
  const document={schema:'wss-test-selection-artifact/v1',producer:{repository:CORE,commit:c.commit,run_id:c.run_id,attempt:1,workflow:WORKFLOW},selection};
  return {created:true,selection,document};
}
// Independent post-upload read. Do not pass prepare's in-memory result directly
// to freeze: uploading, then loading authenticates durable publication first.
export function loadProducerTestSelection(g,m,policy,c,{env=process.env}={}) {
  const attempt=authority(g,m,policy,c,env),saved=existing(g,m,c,attempt);
  requireThat(saved,'Required durable producer test selection artifact is missing');
  return saved.selection;
}
