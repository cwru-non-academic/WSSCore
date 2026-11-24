import {execFileSync} from 'node:child_process';
import {GitHub} from './github.mjs';

const retryDelays = [2_000, 8_000, 20_000];

function sleep(milliseconds) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds);
}

export function retryGitHubRead(read, wait = sleep) {
  for (const delay of retryDelays) {
    try {
      return read();
    } catch (error) {
      // gh reports the HTTP status in stderr. Leave parsing, authorization,
      // missing evidence, and every other failure on their original path.
      const status=/^gh: (?:HTTP (50[234])|[\s\S]* \(HTTP (50[234])\))[\t\r\n ]*$/.exec(String(error?.stderr ?? ''));
      if(!status)throw error;
      console.warn(`GitHub API read returned HTTP ${status[1]||status[2]}. Retrying in ${delay / 1000}s.`);
      wait(delay);
    }
  }
  return read();
}

export class EvidenceGitHub extends GitHub {
  get(path) {
    return retryGitHubRead(() => super.get(path));
  }
  artifactBytes(repository,id,maxBytes) {
    return retryGitHubRead(() => execFileSync('gh',['api',`repos/${repository}/actions/artifacts/${id}/zip`],{env:this.environment,maxBuffer:maxBytes}));
  }
}
