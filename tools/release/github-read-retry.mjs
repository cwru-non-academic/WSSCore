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
      if (!/^gh: (?:HTTP 502|[\s\S]* \(HTTP 502\))[\t\r\n ]*$/.test(String(error?.stderr ?? ''))) throw error;
      console.warn(`GitHub API read returned HTTP 502. Retrying in ${delay / 1000}s.`);
      wait(delay);
    }
  }
  return read();
}

export class EvidenceGitHub extends GitHub {
  get(path) {
    // GitHub.get issues a GET. Downloads and the shared command runner keep
    // their existing behavior, so dispatch and publication cannot be retried.
    return retryGitHubRead(() => super.get(path));
  }
}
