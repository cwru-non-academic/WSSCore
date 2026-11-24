// Repository variables are deliberately distinct from organization/environment
// inheritance. A denied or incomplete read is never equivalent to an unset pin.
import {TEST_VARIABLES} from './test-selection.mjs';
import {requireThat} from '../release/verify.mjs';
const validRepository=value=>typeof value==='string'&&/^[A-Za-z0-9][A-Za-z0-9_-]*\/[A-Za-z0-9][A-Za-z0-9_.-]*$/.test(value)&&!value.includes('..')&&!value.endsWith('.git');
export function readRepositoryTestVariables(g,repository) {
  requireThat(validRepository(repository),'Invalid repository variable owner');
  const names=new Set(),selected={},wanted=new Set(Object.values(TEST_VARIABLES));
  let expected;
  for(let page=1;page<=100;page++) {
    const response=g.get(`repos/${repository}/actions/variables?per_page=30&page=${page}`);
    requireThat(response&&Number.isSafeInteger(response.total_count)&&response.total_count>=0&&Array.isArray(response.variables)&&response.variables.length<=30,'Malformed repository variable response');
    expected??=response.total_count;
    requireThat(response.total_count===expected,'Repository variable count changed while reading');
    for(const variable of response.variables) {
      requireThat(variable&&typeof variable.name==='string'&&/^[A-Z_][A-Z0-9_]*$/.test(variable.name)&&typeof variable.value==='string'&&!names.has(variable.name),'Malformed or duplicate repository variable');
      names.add(variable.name);
      if(wanted.has(variable.name))selected[variable.name]=variable.value;
    }
    requireThat(names.size<=expected,'Unexpected extra repository variables');
    if(response.variables.length<30) {
      requireThat(names.size===expected,'Incomplete repository variable snapshot');
      return selected;
    }
  }
  throw Error('Repository variable pagination limit reached');
}
export function readTestVariableSnapshots(g,policy) {
  requireThat(policy&&Array.isArray(policy.targets)&&policy.targets.length>0,'Missing reviewed test selection target policy');
  const repositories=new Set(),result={};
  for(const target of policy.targets) {
    requireThat(typeof target.id==='string'&&/^[a-z][a-z0-9-]*$/.test(target.id)&&!Object.hasOwn(result,target.id)&&validRepository(target.repository)&&!repositories.has(target.repository),'Invalid or duplicate variable snapshot target');
    repositories.add(target.repository);
    result[target.id]=readRepositoryTestVariables(g,target.repository);
  }
  return result;
}
