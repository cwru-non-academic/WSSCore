# Readiness-only local variable proof (held, not deployed)

Production selection remains OFF. This controller does not invoke a producer,
resolve a campaign, emit a selection artifact, freeze plans, or run selected tests.
The closed readiness registry now binds only the reviewed Python pilot on both
channels; the production registry remains empty. No App grant,
variable write, credential creation or settings change is part of this patch.

## Isolation and actual proof

Core `test-variable-snapshot-readiness.yml` authenticates its real in-progress
workflow_dispatch run at dev/main, immutable workflow source, repository, actor
and closed registry before using the existing App. The only allowed target is
Python_WSS_Integration_Lib, target ID python-wrapper. There are no ref, repository,
settings, candidate or producer inputs. Its reviewed registry entry carries the
source anchor/workflow blob/tooling tree/App actor plus allowed controller actor
IDs. The reviewed controller allowlist is only lemfn64 (25409955); the dispatch
bot is cwru-wss-release-automation[bot] (326213682), whose public App metadata
identifies App 4864404. No other controller or target is authorized. Empty or
malformed registry entries still fail before App use.

The generated receiver workflow is `test-variable-readiness-capture.yml`, not the
production snapshot workflow. It reads exactly the three effective vars, has no
environment or App key, and runs no selected code. Its request has `controller`,
not `producer`; schemas and artifact/run-title prefixes are readiness-only.
Production request, capture and selection validators remain unchanged and reject
these documents. The report states `readiness-only-not-campaign-evidence`.

The controller persists a create-only intent, independently authenticates its ZIP,
then acquires a fixed initial-run marker before POST. Duplicate/uncertain writes
fail closed. Later attempts never prepare, dispatch, collect anew or republish;
they only correlate the original intent and already-existing capture. If a report
exists it must authenticate and match; otherwise `correlate` produces a local
correlation result plus original artifact IDs/digests in the job log, without
uploading/replacing the first-attempt report. Missing, duplicate, unfinished or
failed capture runs remain errors; correlation never reads current vars or causes
a new capture. An uncertain first attempt can therefore be inspected with a real
read-only rerun, not a redispatch. Existing matching capture runs are correlated without another POST.
This is the same trusted-job execution model, not an arbitrary-client exactly-once
service. Both current and retained source identities and final receiver head are
checked. No missing retained anchor may be replaced by the live source.

The controller App token is scoped to the representative receiver with existing
Actions-write/Contents-read; its GITHUB_TOKEN reads Core's own artifacts. Actual
artifact bytes use the existing bounded digest-verified ZIP transport. Reports
contain actual observed settings, local run/artifact IDs and archive/document
digests. No synthetic env substitution is accepted as hosted vars evidence.
The proof does not establish true/false/empty cases unless those actual settings
are observed. Changing repository variables still requires separate approval.

This stage proves App dispatch plus local capture/verified upload/load, not the
production canonical producer path, central selected-ref resolution, ordinary CI
selection, native tests or paired-plan acceptance. Those remain separate evidence.

## Registration and deployment ordering

Read-only GitHub repository metadata confirmed both Core and the representative
receiver default to main. A workflow_dispatch workflow must exist on the default
branch before dispatching it at a reviewed dev ref. An arbitrary SHA is not used
as a dispatch ref.

1. Review the final combined snapshot-plus-readiness dev and main sources, not
   separate producer-changing foundation merges just to register workflows. After
   approval, update or replace the held PR65/66 heads atomically with those sources.
   No publication or dispatch is authorized by the local implementation task.
2. After coordinator approval, port the readiness-only additions onto the reviewed
   main compatibility foundation (draft66). Main requires real controller/module
   closure for a main proof, not merely a placeholder workflow. Do not broad-sync
   dev or import product changes/restart hooks. The full compatibility dependency
   closure is already in draft66; this additive readiness patch depends on it.
3. Retain the approved Core source through canonical tooling and generate the
   representative receiver readiness workflow/modules on main and dev. Only this
   receiver is eligible in the sync manifest; never blindly adopt all eighteen
   branches. Ordinary workflows/fragments remain unchanged.
4. Verify default-branch registration and full workflow/tooling bytes on both
   channels. Retain actual reviewed receiver source anchors, verify numeric App
   and allowed controller actor IDs, and review the two closed registry entries.
   Do not auto-discover and authorize arbitrary heads or actors during a run.
5. Coordinate Core main/dev controller merges and their normal producer side
   effects. Main compatibility merging can trigger its ordinary producer even
   with selection OFF. No empty trigger, stale plan, or fabricated context.
6. With explicit coordinator release, dispatch the real readiness workflow on
   each reviewed branch and inspect its real evidence. No policy activation is
   required. A successful dev proof does not prove main or the other receivers.

## Ordinary caller prerequisites remain separate

The inspected eighteen common-adoption worktrees contained no registered workflow
mapping WSS_LOCAL_TEST_VARIABLES_SCHEMA or invoking ci-test-selection.mjs. Python's
inactive selection fragment still requested Variables-read. That fragment and
stale family READMEs must be migrated in their own reviewed family patches.
Common module adoption alone does not activate or finish ordinary CI selection.

Each actual local resolver needs the four safe env mappings documented in
LOCAL_TEST_VARIABLE_SNAPSHOTS.md, a full verified immutable Core tooling checkout,
existing scoped read authority, create-only context upload followed by independent
load, and all mandatory baseline/native barriers. Preserve frozen campaign reuse.
Do not expose App credentials to selected executors or untrusted PR code. Do not
activate inactive fragments merely to get readiness coverage.

## Prepared pilot identities (no controller publication or dispatch)

Verified Python dev 0487cb6019dc82a67501ad94498344620be4496b and
main 6aeaa3693a8dacc2deea0bc61ec8421c7f875282 after pilot PR 74/75 merged.
Both default-main local capture workflows are registered active. The readiness
workflow blob is c8e8260afaac8baab84cf91482728a1f4bb68db0 and the complete
wss-ci tree is 9e131377717c5f1493f79e1bcb2fbf445687e75d on both live heads and
the independently reviewed sources. Retained source anchors are
d73ccd34f541b640b63d39726dd337c99252b3fc (dev) and
5624c044e653b74826413bcf4586da711043bde5 (main), each under
refs/heads/ci/tooling/<fullSHA>. The registry binds these reviewed executable
identities, not permanently fixed product heads. The actual current receiver
head is authenticated and recorded when a readiness request is prepared.

Public App and bot metadata plus the authenticated gh user endpoint established
the numeric identities; no installation expansion or repository enumeration was
used. These are source-registration prerequisites, not a real App capture proof.
The controller changes remain held for independent review and coordinated
publication. Core merge and readiness dispatch are separate actions.
