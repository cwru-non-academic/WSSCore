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

## Pilot source reconciliation after notifier adoption (local preparation)

The registry's reviewed source commits are the accepted notifier merge results:
- DEV: [PR 81](https://github.com/cwru-non-academic/Python_WSS_Integration_Lib/pull/81),
  d213bfcc4ac5d5ac2a404df56770215e3368c37f.
- MAIN: [PR 82](https://github.com/cwru-non-academic/Python_WSS_Integration_Lib/pull/82),
  52ce2c33c8263dda3e767a537b3762652f631bef.

Both have readiness workflow blob c8e8260afaac8baab84cf91482728a1f4bb68db0
and complete wss-ci tree 4a1a1358da7b87fa47482550b62d0245a4133325. The
former tree 9e131377717c5f1493f79e1bcb2fbf445687e75d differs only in the
accepted notify-family.mjs and tests/notify-family.test.mjs blobs. Both new
blobs match retained canonical notifier source dc3f4ec74a341fa49cadf9798a6c4b4f1f8ab36a;
that canonical source lacks the readiness workflow and cannot replace the full
receiver source authority. MAIN runtime merge cdde6a1b1fa83ec64455bcd435ca0a5ae5d86fb9
preserves these exact executable identities. Moving branch bytes do not authorize
a new registry entry; accepted merge provenance and the full source identities do.

No existing Python ci/tooling anchor contained this workflow/tree pair when checked.
Before deployment, separately approve creation and read-back verification of
refs/heads/ci/tooling/d213bfcc4ac5d5ac2a404df56770215e3368c37f and
refs/heads/ci/tooling/52ce2c33c8263dda3e767a537b3762652f631bef at their matching
commits. Create only missing refs; never replace a conflicting ref. This local
patch does not create either anchor or authorize publication or dispatch. Existing
source checks still require both the reviewed commit and the independently frozen
current receiver commit to match the exact workflow blob and complete tooling tree.

Keep Core publication/merge held until final receiver sources are stable and the
intended fresh campaign launch is reviewed: a Core merge can start ordinary
producer/freeze workflows. Preserve existing plans and receipts. After independent
review, retention and coordinated deployment, separately released DEV and MAIN
readiness runs must authenticate their real local capture evidence. Historical
DEV run 42384127998 is not current-source, MAIN, true-pin or production proof.
The production registry remains empty; acceptance/propagation selection policies remain OFF.
