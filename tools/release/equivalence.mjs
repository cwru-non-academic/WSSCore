import {bytesDigest,canonical,requireThat} from './verify.mjs';

function path(p) {
  requireThat(typeof p==='string' && p.length>0 && !p.startsWith('/') && !p.includes('\\') && !p.includes(':') && p.split('/').every(x=>x && x!=='.' && x!=='..'),'Invalid projection path');
  return p;
}
function unique(paths) {
  paths.forEach(path);
  requireThat(new Set(paths.map(x=>x.toLowerCase())).size===paths.length,'Duplicate/case-colliding projection paths');
}
function inventory(files,expected) {
  unique(Object.keys(files));unique(expected);
  requireThat(canonical(Object.keys(files).sort())===canonical([...expected].sort()),'No-op inventory differs from reviewed projection');
  requireThat(Object.values(files).every(Buffer.isBuffer),'No-op requires actual downloaded bytes');
}
export function projection(p) {
  requireThat(p.schema==='wss-noop-projection/v1' && Array.isArray(p.archive_inventory) && Array.isArray(p.files) && p.files.length>0 && Array.isArray(p.preserved_metadata),'Invalid reviewed projection');
  unique(p.archive_inventory);unique(p.files.map(x=>x.candidate_path));unique([...p.files.map(x=>x.installed_path),...p.preserved_metadata]);
  requireThat(p.files.every(x=>Object.keys(x).length===2 && p.archive_inventory.includes(x.candidate_path)),'Projection contains unreviewed archive paths');
  return p;
}
// Foundation for the separate v2 receipt migration. Not an eligible v1 receipt.
// Compare actual bytes from the exact candidate and both immutable Git sources.
export function fileEquivalence(p,candidate,installed,anchor) {
  projection(p);
  inventory(candidate,p.archive_inventory);
  const expected=[...p.files.map(x=>x.installed_path),...p.preserved_metadata];
  inventory(installed,expected);inventory(anchor,expected);
  const files=p.files.map(x=>{
    const sha256=bytesDigest(candidate[x.candidate_path]);
    requireThat(bytesDigest(installed[x.installed_path])===sha256 && bytesDigest(anchor[x.installed_path])===sha256,`No-op byte equivalence failed: ${x.installed_path}`);
    return {...x,sha256};
  }).sort((a,b)=>a.installed_path.localeCompare(b.installed_path));
  const preserved_metadata=p.preserved_metadata.map(installed_path=>{
    const sha256=bytesDigest(installed[installed_path]);
    requireThat(bytesDigest(anchor[installed_path])===sha256,`No-op changed preserved metadata: ${installed_path}`);
    return {installed_path,sha256};
  }).sort((a,b)=>a.installed_path.localeCompare(b.installed_path));
  return {files,preserved_metadata};
}
export function sourceEquivalence(before,after,ignoredPaths) {
  unique(Object.keys(before));unique(Object.keys(after));
  requireThat(Array.isArray(ignoredPaths) && new Set(ignoredPaths).size===ignoredPaths.length && ignoredPaths.every(p=>['.github/','docs/','README.md'].includes(p)),'Invalid reviewed source exclusions');
  const excluded=p=>ignoredPaths.some(x=>x.endsWith('/')?p.startsWith(x):p===x);
  const select=files=>Object.fromEntries(Object.entries(files).filter(([p])=>!excluded(p)).sort(([a],[b])=>a.localeCompare(b)));
  const a=select(before),b=select(after);
  requireThat(Object.keys(a).length>0 && Object.keys(b).length>0,'Empty source inventory is not source equivalence proof');
  requireThat(Object.values(a).concat(Object.values(b)).every(x=>typeof x==='string' && /^[a-f0-9]{64}$/.test(x)),'Source equivalence requires complete file hashes');
  requireThat(canonical(a)===canonical(b),'No-op source/runtime/test content changed outside reviewed metadata exclusions');
  return {files:Object.keys(a).length};
}
