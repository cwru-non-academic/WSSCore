# No-op equivalence receipt interface, proposed v2

This is a concrete migration design, not enabled receipt support. The current
`wss-acceptance-receipt/v1` publisher rejects every no-op. No owner should emit a
new mode into v1, rewrite existing provenance, or manufacture an empty dependency
PR. Normal v1 `merged-pr` receipts remain unchanged.

## Identities

A future reviewed `wss-acceptance-receipt/v2` has `mode: "equivalent-no-op"` and
must distinguish:

* `acceptance_sha`: the exact current dev commit, tested by fresh normal CI and
  used by the successful receipt emitter; this can include reviewed tooling work.
* `merge`: the actual prior bot dependency PR, human merge actor, PR head and merge
  SHA. It anchors installed bytes and original provenance; never call a migration
  PR a dependency promotion. GitHub must prove this anchor remains an ancestor of
  `acceptance_sha` and has not been reverted or rewritten.
* `candidate`: the new frozen Core tuple, bound to fresh full route preflights.
* `equivalence.original_candidate`: the old six-field tuple read from unchanged
  installed provenance at both anchor and acceptance commits. The original
  approval, original paired proof and source scope must remain valid.

Children keep their existing gitlink only if it equals the accepted parent's
dependency anchor **and** a full source/runtime equivalence comparison proves that
the pinned parent and current parent have identical runtime, project and test
content. Fresh application preflights must exercise that actual committed pin.
If wrapper source/configuration changed, open a normal reviewed gitlink PR instead.
All-clear records must display acceptance and dependency identities separately.

## Frozen projection and actual bytes

Each owner supplies a reviewed `wss-noop-projection/v1` policy with:

```json
{
  "schema": "wss-noop-projection/v1",
  "archive_inventory": ["every exact canonical archive path"],
  "files": [{"candidate_path":"backends/macos/libwss_macos_ble.dylib", "installed_path":"the reviewed installed path"}],
  "preserved_metadata": ["every existing provenance/importer metadata path"]
}
```

Core must freeze the complete policy and digest into the plan. C#/Python full
archives and Unity's explicit projection need separate policies; do not generalize
by accepting additional files. Owner validators retain ABI/load/importer and all
existing canonical/native application checks.

`fileEquivalence` in `equivalence.mjs` tests the byte/inventory foundation: require
the exact archive inventory and exact installed projection; hash actual candidate,
accepted Git blob and original anchor bytes; every selected file must match all
three. Preserve every metadata byte, including the old approval and Unity importers.
Reject traversal, aliases, duplicates, extras, missing/corrupt Mac DLL/dylib pairs,
hash-only claims or changed provenance. The reviewed projection is policy, never a
mapping nominated by an untrusted receipt.

Source equivalence compares complete Git inventories and file hashes after removing
only the exact payload/metadata files already proved above. Only reviewed `.github/`,
`docs/` and `README.md` metadata exclusions are eligible. Runtime code, projects,
dependencies and canonical tests must match; truncated tree/diff evidence fails.

The v2 `equivalence` record must retain `projection_sha256`, `original_candidate`,
the selected file list with candidate/installed paths and SHA-256, preserved metadata
digests, source-comparison commit/tree identities and the verified original anchor.

## Proof and activation sequence

1. Freeze all receiver code, normal CI and projection policies before candidate
   handoff. Mixed v1/v2 support requires an explicit required-schema capability map;
   never omit a required receiver because its mode is not supported.
2. Collect fresh candidate preflights even when the existing promotion exits early
   for identical payload. Preserve richer Python/ROS/Unity and paired C# evidence;
   additionally retain the normalized six-field candidate artifact.
3. Validate original anchor/approval and full installed-byte/source equivalence.
   Run fresh normal CI at `acceptance_sha`, with required jobs and actual pinned
   dependency selection. The receipt writer performs all existing route validators.
4. Core independently downloads exact candidate and Git blob bytes, checks ancestry,
   code/plan/artifact/run identities, normal CI and parent equivalence. A JSON claim
   or empty/no-op dispatch result is never acceptance.
5. Enable v2 only after producer/collector plus every required receiver implements
   this contract and rejection tests pass. Until then, v1 rejects no-op receipts.

Current tests cover the primitive inventories/bytes/source boundaries and the v1
publisher's no-op rejection. They do not constitute deployed v2, actual GitHub
ancestry, route preflight or end-to-end acceptance evidence.
