# Shared CI migration

## Decision

Core is the reference repository. New common CI orchestration uses Node 24 and
the actual Core modules, copied mechanically into all nine receivers. Existing
PowerShell archive inspection remains one shared implementation. Language code
is an adapter for execution, projection and rich proof validation, not a second
implementation of generic policy.

The first migration shares these real execution paths:

| Concern | Canonical source | Consumers |
| --- | --- | --- |
| Frozen plan, candidate, manifest and digest validation | `tools/release/verify.mjs` and `verify-plan.mjs` | C# and Unity receipt writers |
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
2. Run `node tools/ci/sync-tooling.mjs copy <workspace-containing-all-nine-checkouts>`.
   The command copies only manifest-owned files and records the Core commit.
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

These are subsequent coordinated migrations, not claims of completion in this
first PR set. In particular, this change does not refresh the old receipt
contract pin, homogenize evidence schemas or enable a new producer.

Keep MATLAB native version/architecture proof, Python/pythonnet/ROS Humble proof,
Unity importer and Editor versus .NET harness distinctions, C# packaged/native
proof, actual candidate bytes, human merge requirements and historical digest
encodings. A normalized candidate tuple cannot replace the richer proofs.

Core README PR #33 stays held. No merges, dispatches, security-setting changes,
new permissions or publication are part of this migration.
