# Shared CI migration

## Decision

Core is the reference repository. New common CI orchestration uses Node 24 and
the actual Core modules, copied mechanically into all nine receivers. Existing
PowerShell archive inspection remains one shared implementation. Language code
is an adapter for execution, projection and rich proof validation, not a second
implementation of generic policy.

The proposed common bundle shares these execution paths. Installation into a
receiver is complete only when its lock and call sites reference this version:

| Concern | Canonical source | Consumers |
| --- | --- | --- |
| Frozen plan, candidate, manifest and digest validation | `tools/release/verify.mjs`, `github.mjs::loadPlan`, `receipt.mjs` | All four receipt families |
| Complete current producer jobs | `tools/release/producer-jobs.mjs` and `tools/ci/shared/Producer.ps1` | All four promotion adapters; exact-attempt current jobs, explicit historical inventory only |
| Receipt assembly, exact merge/source, live run/attempt and rich-proof orchestration | `tools/release/receipt.mjs`, `receipt-targets.json` | All nine targets through adapters |
| Six-field normalized provenance | `tools/release/normalize-candidate.mjs` | All four wrapper resolvers; diagnostic tag metadata stays in selection.json |
| Promotion PR publication | `tools/release/promotion.mjs` | All four families after adapter staging |
| Staged producer policy and uncertain-dispatch rules | `tools/release/staging.mjs`, `dispatch-proof.mjs` | All four fanout adapters |
| Typed evidence fields, file hashes, architecture pairing and native argv | `tools/ci/shared/Evidence.ps1`, `Process.ps1` | Existing runtime-specific evidence adapters |
| Actual-merge CI selection | `tools/release/normal-ci.mjs` | C#, Unity, Python and MATLAB receipt writers |
| ZIP path/type/size/hash validation before extraction | `tools/ci/shared/Assert-UniversalArchive.ps1` | Core projection reader and all four wrapper resolvers |
| Copying, immutable version lock and identity check | `tools/ci/sync-tooling.mjs` and `.json` | All nine receiver repositories |

Select exactly one eligible postmerge push/manual CI run. Multiple eligible
runs are ambiguous even if the newest passed. This deliberately adopts Core's
existing fail-closed acceptance rule; recovery needs inspection, not a hidden
latest-success fallback. Existing job names and runtime coverage stay unchanged.

Core already has tests for candidate/plan validation, ambiguous committed-state
CI and dispatch journals. The new hosted matrix checks copy drift/forged locks
and malformed archives on Windows, Linux and macOS. No local test suites are
required for this rollout.

## Update process

1. Change and commit the common source in Core on a dev feature branch.
2. Preview local copies with `node tools/ci/sync-tooling.mjs copy <workspace-containing-all-nine-checkouts>`.
   Retain the reviewed source as `ci/tooling/<Core-commit>` independently of the
   feature branch; execution remains pinned to the literal commit SHA. Review
   and publish each receiver diff into its authorized draft PR. The local copier
   performs actual updates across all nine receivers, not only drift detection.
   `publish-tooling.mjs` is proposed bulk publication tooling; it has not been
   demonstrated successfully and is not the validated rollout mechanism. Its
   presence is not permission to invoke it. Inspect any uncertain remote outcome
   before retrying; never merge or dispatch as part of synchronization.
3. Adapt only the receiver-owned call sites, configuration and native proof code.
   Open coordinated draft PRs into dev. Run hosted normal CI and source-identity
   checks. A Core update is not deployed merely because its PR passed.
4. After review, merge dev changes and promote dev to main through reviewed PRs.
   Independently register main workflows only when GitHub requires registration.
   Receipt emitters executing trusted main must match the accepted frozen helper
   identity; dev-only installation is intentionally not enough.
5. Refresh all frozen tooling baselines and application validator pins together.
   Existing plans predate the changed `.github/scripts` tree and cannot be reused.
   Generate fresh guarded candidate proof only after the user approves rollout.

The lock stores SHA-256 of Git blob content. Checks use Git's clean filters so
normal CRLF Windows checkouts and LF Unix checkouts identify the same source.
No global Git configuration or repository-wide EOL conversion is needed.

The durable source ref is independent of the feature branch. Its recorded commit
must match the checked-out Core HEAD; moving the ref cannot authorize different
code under an existing lock. Preserve these refs after squash merge or feature
branch deletion. Python/MATLAB receipt adapter checkouts receive their own
durable refs, separate from runtime dependency gitlinks.

## Evidence stages and current blockers

Reviewed PR-head CI proves the proposed dependency-only change against its
preflight source, before an actual target merge exists. Receipt acceptance is a
later gate: it requires the actual human merge, exact current merged tree and
postmerge push/manual CI. These are distinct evidence stages, not alternate
language implementations. Keep their event/source associations explicit; never
use PR-head evidence alone as a release acceptance receipt. Generic identity,
job and ambiguity checks should continue to be extracted without confusing the
two stages.

The common receipt engine imports the copied current Core verifier, not the
legacy `be8546c` checkout. Its loadPlan still binds the downloaded plan to the
canonical producer and the policy at that producer commit. Old contract
checkouts in existing YAML do not select the new engine's verifier.

The November 5 follow-up implements one nested
`wss-ci-evidence-identity/v1` record in `Evidence.ps1`, binding repository, run,
attempt, source and canonical architecture. C#, Python, Unity and MATLAB native
adapters produce and validate it while retaining their runtime and file-hash
checks. Remote preflight validation supplies the actual selected run attempt;
hosted fixtures reject another attempt. Old committed-payload diagnostics can
precede this contract, but cannot authorize current candidate proof. Fresh
guarded candidate reconciliation still requires coordinated deployment and a
new frozen plan; ordinary PR CI is not that end-to-end proof.

The November 4 review found initial six-file locks. The expanded manifest now
contains 23 shared files, including the generated identity workflow. Compare
each current PR head and lock against its exact retained Core source; earlier
green heads do not verify later adoption changes. WSS_Documentation's November
5 shared-implementation guide supersedes its explicitly historical October 24
workflow inventory as a description of this proposal, not deployed readiness.

## Audit and remaining migrations

The November 4 audit inspected Core dev `db6616a`, main `9a3fddb`, C# dev
`c2dab61`, Unity dev `5704171`, Python dev `9b37f97`, MATLAB dev `90795f7`,
plus their application repositories and independent language audit findings.

| Area | Finding | Next common implementation boundary |
| --- | --- | --- |
| Receipt assembly | Python/MATLAB Node writers are nearly identical; C#/Unity repeat orchestration in PowerShell. | Extract one Node writer after all rich-proof adapter inputs are explicit. Preserve paired proof counts, source snapshots, readiness and merged-wrapper application preflights. |
| GitHub evidence transport | Core owns bounded pagination, exact attempt jobs, unique artifact inventory and tree identities; receiver helpers repeat subsets. | Move callers to Core `github.mjs` with separate read-only and write-capable transports. Receipt transport must continue rejecting mutation. |
| Producer policy | Several receiver lists omit Core's three BLE aggregate jobs, and receipt contracts still pin old Core `be8546c`. | Version adoption of Core `producer()` with all required jobs/current-source rules; separate historical diagnostics from propagation. |
| Preflight/promotion/fanout | Receiver scripts repeat source/intent/barrier checks with different approval schemas. | Extract generic journal/correlation and barrier checks from existing Core code. Preserve the four-wrapper barrier, late revalidation, credential separation and uncertain-dispatch handling. |
| macOS summaries/evidence | Repeated aggregators differ in run/attempt/source bindings and schema. | Use Core's exact-attempt job gate, then runtime-specific envelope adapters retaining the strongest fields. Version envelope changes. |
| Payload inventories | Hardcoded legacy/macOS inventories and projection mappings differ. | Generate reviewed inventory from the pinned Core manifest; keep language mapping/importer rules as explicit adapters. |
| Candidate normalization and argv | C# resolver adds `release_tag` to the six-field normalized record; older promotion/resolver/fanout helpers retain positional-array forwarding. | Move the six-field writer and argument-safe process boundary into the common reference, with golden-record and real-argv hosted regressions before adoption. |

This table records the audit boundaries, not a claim that all migrations are
deployed. The new receipt engine and shared publication/dispatch primitives are
implemented in the reference. Follow-up adapters also share the producer job
policy, native identity envelope and named process arguments. Remaining
deployment/pin refresh and fresh guarded candidate proof are explicit gates.
No new producer is enabled by these helpers.

Keep MATLAB native version/architecture proof, Python/pythonnet/ROS Humble proof,
Unity importer and Editor versus .NET harness distinctions, C# packaged/native
proof, actual candidate bytes, human merge requirements and historical digest
encodings. A normalized candidate tuple cannot replace the richer proofs.

Core README PR #33 stays held. Core #34 must also remain held until receiver
readiness and a new producer are coordinated: merging into Core dev can invoke
the existing automatic downstream handoff. Review Core first, but do not infer
that it is safe to merge first. The retained source SHA lets receiver drafts
adopt identical code before a Core dev merge. No merges, dispatches,
security-setting changes, new permissions or package publication are performed
as part of this migration.
