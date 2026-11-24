import {CORE,canonical,digest,requireThat} from './verify.mjs';

// GitHub hides bypass actors from read-only tokens. Administrative inspection
// must see the complete list. Runtime checks pin that reviewed policy revision.
function reviewedTagPolicy(r,rules,bypassActors,allowHiddenBypass) {
  requireThat(r && String(r.id).match(/^[1-9][0-9]*$/),'Missing tag ruleset identity');
  requireThat(r.target==='tag' && r.enforcement==='active','Version guard must be an active tag ruleset');
  requireThat(r.source_type==='Repository' && r.source===CORE,'Version guard must be this repository policy');
  requireThat(canonical(r.conditions)===canonical({ref_name:{include:['refs/tags/v*'],exclude:[]}}),'Version tag guard must cover every v* tag without exclusions');
  const matchesBypass=Array.isArray(r.bypass_actors)&&canonical(r.bypass_actors)===canonical(bypassActors);
  requireThat(matchesBypass||allowHiddenBypass&&!Object.hasOwn(r,'bypass_actors'),bypassActors.length?'Version tag creation must allow only the WSS publisher App':'Version tag guard must have no bypass actors');
  const updated=typeof r.updated_at==='string'?Date.parse(r.updated_at):NaN;
  requireThat(Number.isFinite(updated),'Version tag guard is missing its policy revision');
  requireThat(Array.isArray(r.rules)&&canonical([...r.rules].sort((a,b)=>a.type.localeCompare(b.type)))===canonical(rules),'Version tag rules differ from the reviewed policy');
  return {id:String(r.id),target:r.target,enforcement:r.enforcement,source_type:r.source_type,source:r.source,
    conditions:r.conditions,bypass_actors:bypassActors,rules,updated_at:new Date(updated).toISOString()};
}
export function tagPolicy(r,{allowHiddenBypass=false}={}) {
  return reviewedTagPolicy(r,[{type:'deletion'},{type:'update'}],[],allowHiddenBypass);
}
export function tagCreationPolicy(r,publisherAppId,{allowHiddenBypass=false}={}) {
  requireThat(typeof publisherAppId==='string'&&/^[1-9][0-9]*$/.test(publisherAppId)&&Number.isSafeInteger(Number(publisherAppId)),'Version tag creation requires an exact publisher App ID');
  return reviewedTagPolicy(r,[{type:'creation'}],[{actor_id:Number(publisherAppId),actor_type:'Integration',bypass_mode:'always'}],allowHiddenBypass);
}
function matchTagGuard(g,guard,parse) {
  requireThat(guard && typeof guard.ruleset_id==='string'&&/^[1-9][0-9]*$/.test(guard.ruleset_id)&&/^[a-f0-9]{64}$/.test(guard.policy_sha256 || ''),'Version tag protection is unconfigured; publication is blocked');
  const policy=parse(g.get(`repos/${CORE}/rulesets/${guard.ruleset_id}`));
  requireThat(policy.id===guard.ruleset_id&&digest(policy)===guard.policy_sha256,'Version tag protection changed from reviewed policy');
  return {ruleset_id:policy.id,policy_sha256:digest(policy)};
}
export function checkTagGuard(g,m,{versioned=false}={}) {
  const exact=matchTagGuard(g,m.legacy_tag_guard,r=>tagPolicy(r,{allowHiddenBypass:true}));
  const creation=m.version_tag_creation_guard;
  if(versioned||creation!==undefined) {
    requireThat(creation?.ruleset_id&&creation.ruleset_id!==exact.ruleset_id,'Separate version tag creation protection is unconfigured');
    matchTagGuard(g,creation,r=>tagCreationPolicy(r,creation.publisher_app_id,{allowHiddenBypass:true}));
  }
  return exact;
}
