# Dev release acceptance contract v1 — implementation interface

This is the shared interface for the Core, C#, Unity and Python/ROS owners. Only
`dev` is implemented first. Main/stable remains disabled until dev is validated.
MATLAB is explicitly excluded from both required acceptance and dispatch.

## Frozen plan (Core owns)

The successful canonical `artifacts.yml` **dev push** produces a separate
`wss-propagation-plan` Actions artifact. Do not add files to
`universal-release-assets`: its ZIP/checksum two-file contract is unchanged.

`plan.json` uses schema `wss-propagation-plan/v1` and contains `channel: "dev"`,
the existing six-field `candidate` tuple, a complete frozen `manifest`, its
`manifest_sha256`, `target_snapshots`, and `plan_id`. Digests use SHA-256 of UTF-8
JSON with recursively sorted object keys and no insignificant whitespace. `plan_id`
is the digest of the plan excluding `plan_id`. Snapshot every required target's
dev head and Git object IDs for its `trusted_paths`. Missing/unregistered receiver
tooling blocks plan creation; it is not an implicit exclusion.

The required target IDs are `csharp-wrapper`, `csharp-cli`, `unity-wrapper`,
`unity-gui`, `python-wrapper`, `python-cli`, `python-ros`. Required targets are
immutable for that plan. `matlab-wrapper` and `matlab-cli` are explicitly excluded
with `dispatch: false`; changing this needs a later reviewed policy/version.

## Acceptance receipt (each owner owns its receivers)

Add `.github/workflows/release-receipt.yml` and a trusted receipt writer. Publish a
single JSON file `receipt.json` in an artifact named
`wss-acceptance-<target_id>-<plan_id>-a<run_attempt>` after actual human merge and
successful normal committed-state CI. An explicit reconciliation dispatch may
recheck an existing merge/no-op; it must not create a dependency PR or merge one.
The receipt writer needs read access only. Deploy receipt tooling through reviewed PRs
to default/main and dev before Core enables plan production.

The shared reconciliation entry point is `workflow_dispatch` with required string
inputs `producer_run_id` and `plan_id`. Its exact run-name is
`WSS acceptance <target_id> <plan_id>`. The writer downloads the producer's frozen
plan and rejects an input plan ID mismatch. Core may request this read-only receipt
once per target/plan. Existing pending/successful attempts are reused; a failed
attempt requires explicit inspected recovery, never a duplicate automatic retry.

Receipt envelope:

```json
{
  "schema": "wss-acceptance-receipt/v1",
  "plan_id": "<64 hex>",
  "channel": "dev",
  "target_id": "csharp-cli",
  "candidate": {"repository":"cwru-non-academic/WSSCore","commit":"<40 hex>","run_id":"1","artifact_id":"2","archive":"WSS-Universal-ci.zip","sha256":"<64 hex>"},
  "mode": "merged-pr",
  "merge": {"pr_number":"15","head_sha":"<40 hex>","sha":"<actual API merge SHA>"},
  "normal_ci": {"run_id":"3","attempt":1},
  "preflights": [{"repository":"cwru-non-academic/CLI_CSharp_WSS_Application","run_id":"4","source_sha":"<40 hex>","workflow":".github/workflows/wss-preflight.yml"}],
  "parents": {"csharp-wrapper":"<actual accepted wrapper merge SHA>"},
  "emitter": {"run_id":"5","attempt":1,"source_sha":"<same actual merge SHA>","workflow":".github/workflows/release-receipt.yml"},
  "coverage": ["canonical-conformance","packaged-macos-ble-load"]
}
```

The writer must first invoke existing receiver-specific approval/projection,
candidate checksum, paired preflight, actual merge, normal CI, source-scope and
payload/gitlink validators. JSON claims alone are not authority. Core independently
checks GitHub PR/run/artifact identity, exact current branch state, required jobs,
parent gitlinks, and the frozen receipt tooling Git IDs at the actual merge.
Each preflight listed in the receipt must additionally retain the normalized
`wss-candidate-provenance` artifact with exactly one `candidate.json` file containing
the six-field candidate tuple. C# already has this contract. Python/ROS and Unity
must preserve all existing richer/native/projection evidence and its validators;
the normalized artifact is an additional cross-language binding, not replacement
evidence. Do not activate until each registered preflight emits it after success.
The receipt emitter must be a successful same-repository `workflow_dispatch` run
at the actual accepted dev merge, with exact attempt and a unique unexpired
artifact. Fork/caller-nominated code is never an eligible emitter. Normal CI must
also be a fresh `workflow_dispatch` (or `push`) run at the actual accepted merge;
PR-head API identities cannot establish what synthetic merge checkout was tested.
Core reconciliation may request existing normal workflows before the receipt.

Preserve existing no-op provenance. A no-op must be explicitly identified and
anchored to a real prior reviewed merge plus fresh full candidate-byte/projection
equivalence and normal-state proof; it must not manufacture a metadata-only PR.
Do not emit a `merged-pr` receipt for a dispatch, open PR, synthetic fixture,
preflight-only run, skipped check or unverified no-op. Initial publisher fails
closed on no-op receipts until equivalence verification is implemented/tested.

## Responsibilities and rollout

* Core: manifest/plan creation, receipt collection and independent verification,
  all-target barrier, exact ZIP preservation, guarded beta publication and replay.
* C# owner: wrapper and CLI receipt writers, preserving schema-2 approval/proof.
* Unity owner: wrapper and GUI writers, retaining native/projection/actual GUI
  validators. Current .NET harness coverage must not claim Unity Editor execution.
* Python owner: wrapper, CLI and ROS writers; retain v2 fan-out, ROS native/platform
  and post-merge preflight evidence. No weakened ROS requirement.

Default-required normal check names are in `targets.dev.json`; owners must confirm
them before activation. Extra success cannot compensate for a missing required
check. Capabilities and target exclusions must be visible in the release record.

Receipt failure/missing/expired/ambiguous/source-stale evidence blocks publication.
Partial human merges stay in place; fixes/reverts are new reviewed PRs, never a
cross-repository reset. Retry only the same immutable plan, reuse valid exact proof,
and inspect uncertain outcomes. An existing matching release is a verified no-op;
an existing mismatched tag/release blocks. No forced tag, overwrite or duplicate.

The old `v*` publisher must be disabled/rejected. Release code will publish the
accepted ZIP bytes (possibly under a release asset alias) and record the original
candidate identity, actual acceptance merges, proof digests and plan/manifest IDs.
No rebuild at release. Main/stable and enabling MATLAB are separate later stages.
