# Shared CI source and deployment

## Source ownership

Common orchestration uses Node.js 24 ECMAScript modules and PowerShell 7 bridges.
Core owns validation and archive policy used by Core and receivers. The Python
integration library owns orchestration used only by integrations and applications.
The manifest in `tools/ci/sync-tooling.json` declares each origin, immutable source
revision, source path and receiver scope. Receiver files are actual copies of
these sources, not separate implementations in each product language.

| Concern | Canonical source |
| --- | --- |
| Candidate, frozen plan, producer, archive and policy validation | Core `tools/release/verify.mjs`, `github.mjs`, `policy.mjs` |
| Current producer jobs and unique committed-state CI | Core `tools/release/producer-jobs.mjs`, `normal-ci.mjs` |
| Shared archive bridge, copier and identity workflow | Core `tools/ci/shared/Assert-UniversalArchive.ps1`, `sync-tooling.mjs`, manifest and shared workflow |
| Receipt orchestration, target profiles and use of the Core plan validator | Python integration `.github/scripts/wss-ci/receipt.mjs`, `receipt-targets.json`, `verify-plan.mjs` |
| Candidate normalization and constrained PR publication | Python integration `normalize-candidate.mjs`, `promotion.mjs` |
| Staging, durable dispatch proof and adapters | Python integration `staging.mjs`, `dispatch-proof.mjs` and associated profiles/bridges |
| Process arguments and versioned native evidence identity | Python integration `Process.ps1`, `Evidence.ps1` |
| Correlated preflight requests and exact-run waiting | Python integration `preflight.mjs`, `Preflight.ps1` |
| Integration-only coordinator notification | Python integration `notify-family.mjs`, `notify-core-stage.yml` |

Integrations receive 27 managed files and applications receive 25; notifier files
are scoped to integrations. Preserve receiver runtime tests, projection/importer
rules, native evidence interpretation, explicit executable pins, job names and
application gitlinks. The shared source does not replace language-specific proof.

## Update procedure

1. Change the owning source once and verify the exact revision with hosted CI.
2. Commit and retain the declared source revisions. From the reviewed Core
   checkout, run `node tools/ci/sync-tooling.mjs copy <workspace>` with all nine
   receivers and the referenced integration source available in the workspace.
3. Review the changed files and locks, including necessary receiver-owned call
   sites and executable pins. Do not independently edit generated common files.
4. Use reviewed receiver dev PRs, hosted source-identity/platform checks and normal
   dev-to-main promotion. Verify workflow registration and actual deployed refs.

The copier performs local file and lock updates. External pushes, PR creation,
merges and campaign activation remain separate authorized operations. Source
identity checking detects drift; it does not by itself deploy an update.

Locks record literal Core and integration source SHAs, per-file origin and
SHA-256. Retain `ci/tooling/<commit>` anchors and pinned adapter revisions after
feature deletion or squash merges. Git clean filters compare canonical blobs,
so ordinary Windows CRLF and Unix LF checkouts have the same source identity.

## Proof and trust boundaries

Select exactly one eligible committed-state push/manual CI run after the actual
dependency merge. Multiple eligible runs are ambiguous, even when the newest is
green. Required jobs belong to the selected attempt; do not combine unrelated
attempts into proof. Promotion PR-head checks and actual postmerge receipts are
distinct stages.

The common publisher clears inherited multi-valued Git authorization headers
before supplying one delayed repository-scoped token. Its hosted contracts use
a real Git HTTP endpoint on Linux, Windows and macOS. Read-only evidence tokens
remain separate from authoring/dispatch credentials. Durable intent precedes
dispatch, and uncertain outcomes require inspection before retrying.

Trusted-main reconciliation supports per-family authorization for frozen policies
declaring `application_fanout: per-integration`. Each family's reviewed dependency
merge and unique successful committed CI authorize only its linked applications.
Older frozen policies retain their all-integration barrier. Final acceptance
still requires all applicable nine targets. The notifier is a guarded wake-up,
not acceptance or release authorization.

Changed tooling invalidates old frozen baselines; old candidate proofs cannot be
reused after source adoption. A green source PR or registered workflow does not
establish an end-to-end campaign or a published release. Publication controls and
coverage limits remain enforced. Unity .NET harness proof is not an Editor run;
native BLE ABI/load proof is not physical Bluetooth or stimulation proof.
