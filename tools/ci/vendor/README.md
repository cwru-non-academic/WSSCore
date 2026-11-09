# Pinned workflow YAML parser

The complete official yaml 2.9.1 package is retained unchanged under yaml/. The inventory imports yaml/dist/index.js relatively; no runtime install, registry lookup or download occurs. Upstream LICENSE, package metadata, browser files, Node runtime and declarations are intact. yaml.provenance.json records the distribution URL, npm SHA-512 integrity and SHA-256/byte length of all 233 retained files (686,297 bytes). The scoped Git attribute preserves those published bytes on every platform.

Update only in a reviewed source change:

1. Use npm pack yaml@<exact-version> --json in external scratch space. Record its official registry URL and integrity. Verify the downloaded archive SHA-512 against that integrity before extracting. Do not run package lifecycle scripts.
2. Extract the complete package into a fresh scratch directory, stripping only the package/ prefix. Compare the complete path set and every file byte with the proposed vendor subtree. Do not hand-trim or edit upstream files.
3. Regenerate the retained-file hashes/lengths in yaml.provenance.json and record the new exact upstream version, URL, integrity and license. Preserve the scoped -text attribute.
4. Run adapter-pins.test.mjs, including vendor integrity, alias limits, exact source spans and reduced synthetic Core runtime closure, then inventory every immutable fleet workflow before adoption.

Parser syntax is separate from source admission: adapter-pins.mjs retains the finite role catalogue and existing verification/refresh policy. It rejects duplicate keys, merges, tags and multiple documents. Existing aliases resolve through upstream ASTs with explicit cycle, depth and expansion bounds; every consuming job remains a distinct source occurrence.
