# Common receipt invocation

The Python integration repository owns `.github/scripts/wss-ci/receipt.mjs`,
`receipt-targets.json`, `receipt-workflows.mjs` and `receipt-workflow.yml`.
Core consumes those immutable sources through the existing source-copy manifest
and renderer. Core retains the independent final nine-target acceptance check.
There is no second receiver engine or native evidence policy.

## Execution contract

Every generated local receipt has only `producer_run_id` and `plan_id` inputs.
Its fixed sequence is static caller eligibility, immutable Python bootstrap and
Core guard checkouts plus exact accepted receiver checkout, the existing
family-scoped read credential, common authority admission, native adapter,
common proof/recheck/emission, and successful-result-only artifact upload.
The bootstrap SHA is a reviewed trust anchor, not a sandbox. Its self-identity
check cannot secure arbitrary code that already ran. Remote authority checks
need the existing read credential and finish before native adapter execution.
No App permission, installation, repository setting or activation changes are
part of this migration. The checked-in 18-record permission audit in the owner
records unchanged workflow permissions and unchanged family token repositories.

The config has exactly seven absolute root roles: `sourceRoot` (accepted
receiver), `engineRoot` (immutable Python owner), `coreRoot` (immutable Core),
`toolsRoot` (registered native adapter owner), `validatorRoot` (wrapper tooling),
`integrationRoot` (actual accepted wrapper product), and fresh `outputRoot`.
Aliasing is allowed only in registry-declared groups; wrapper native roles must
all alias its accepted root. App wrapper tooling is pinned independently from
the accepted wrapper product. Accepted receiver and accepted integration
checkouts use full Git history (`fetch-depth: 0`): native source assertions compare
the frozen preflight source against the later dependency merge. Shallow
checkouts omit legitimate ancestor objects and cannot satisfy those assertions.
Immutable tool-only checkouts remain pinned to their exact revisions.
All Git roots must be clean, including untracked
files. Pins, copied-source locks, frozen scripts/config/workflows, exact caller,
producer/archive/jobs, actual merge, parents and normal CI are admitted before
native code. Source-bound proof descriptors are checked unconditionally, not
only when selected tests are enabled. Root/source/normal-CI movement is rechecked.

Adapters receive `-ContextPath`, `-WorkRoot`, `-OutputPath`; C#/Unity also receive
`-TargetId`, `-ProducerRunId`, `-PlanId`, and Python/MATLAB `-ToolsRoot`.
Context preserves `plan`, `target`, `head`, `pr`, `parents`, `paths`.
C#/Unity adapters no longer launch/reenter Node. Legacy Python/MATLAB four-path
Node frontends and Python app PowerShell shims are retired fail-closed.
All rich native proofs/counts remain mandatory, including actual-wrapper tests,
ROS Humble, MATLAB native releases and Unity harness-not-Editor distinctions.
Failures identify eligibility, roots, pins, authority, adapter, proof, recheck
or emission; no failed adapter can write the common receipt or upload it.

## Immutable adoption order

1. Commit/review retained integration implementation source P. Its previous
   consumer copy lock is not a claim that this authoring commit is an adopted
   product checkout. Do not activate a campaign from this source-only commit.
2. Commit/review Core tooling source C with the distribution origin pinned to P.
   The common engine calls C's existing extended workflow-freeze authority through
   the validated Core root. A source-closure test proves every Core-owned Node
   helper imported from P is byte-identical to the chosen C source; differing
   product-channel origin labels are not silently treated as equivalent.
3. Keep a separate clean origin workspace containing
   `Python_WSS_Integration_Lib` checked out exactly at P. For each target workspace:
   `node tools/ci/sync-tooling.mjs copy <targets-workspace> <origins-workspace>`.
   Verify with `check <repository> <checkout> <origins-workspace>`.
4. Generate wrapper receipts with
   `node tools/ci/channel-workflows.mjs receipt-write <repository> <checkout> <origins-workspace> <P>`.
   Complete wrapper native/source-copy/pin adoption and commit reviewed wrapper W.
5. Refresh other existing app workflow pins using the matching wrapper W/lock,
   then generate app receipts with the same command and W as validator SHA.
   `adapter-pins refresh` preserves generated receipt text; `receipt-write` must
   regenerate it, followed by `receipt-check`. Never regex-edit receipt pins.
6. After reviewed consumer adoption on both channels and independent final
   comparisons, freeze NEW receiver sources before a NEW producer. Existing
   stopped campaigns, plans, artifacts and intent journals are immutable history.

Retaining P/C does not authorize a protected Core branch push or publication.
A source-only P/C pair is not all-nine adoption. MAIN must also preserve the
still-held Core readiness/notifier reconciliation dependency (PR71); do not
silently replace that work or treat current MAIN as already equivalent to DEV.
No old receipt rerun, campaign merge, selection activation or publication is
part of these local implementation changes.

## Pure verification

Run owner `node --test .github/scripts/wss-ci/tests/receipt.test.mjs` with
`WSS_CORE_REFERENCE` set to the candidate Core checkout. The test uses generated
workflows, immutable fixture Git sources and real pwsh argv invocation with stub
native proof output, for nine roles on DEV/MAIN. It checks sentinel ordering,
closed roots, permissions, errors, output suppression and Unicode/spaced paths.
It does not execute WSS DLLs or hardware. Native family negative suites are
separate and must be rerun against the final composed consumer files; a stub
receipt test alone is not proof of the complete native proof implementation.
The C# native receipt suite must also exercise the unchanged real
`Assert-WssSource` against real shallow/full local Git fetches: missing history
fails, full legitimate source comparisons pass, and prohibited source changes
still fail. Common template tests cover accepted/integration history across
all nine DEV/MAIN roles. Do not substitute stub adapter success for this native
history regression.
Core workflow-freeze, source-copy, executable-pin, renderer and independent
acceptance suites must also pass. Hosted normal checks follow reviewed adoption.

## Native attempt and evidence binding

The native validator observes the successful run attempt before reading its
attempt-specific jobs. Receipt-only capture preserves that exact observation,
not a later current-attempt value. Existing family helpers remain the sole
owners of required job names, native release/runtime assertions, paired source
checks and actual merged-wrapper proof semantics.

A strict preflight descriptor carries repository, run ID, source SHA, workflow,
positive integer attempt, the complete canonical attempt job snapshot (job ID,
name, status and conclusion), exact validated artifact ID/name/ZIP SHA256 bindings,
and any validated job-log ID/raw-byte SHA256 bindings. The common capture bridge
records actual reads automatically. Native helpers complete the descriptor only
after their existing rich checks succeed. The common engine and independent Core
collector authenticate the same attempt and bindings again; Core rechecks all
nine targets before producing acceptance. A successful later rerun cannot inherit
an earlier attempt's native validation. Failed, skipped or duplicated required
jobs remain rejected by the existing native policy, not a second Core job table.

Exact artifact-ID transport verifies GitHub's ZIP digest before bounded safe
extraction (64 MiB ZIP/file, 256 MiB expanded, 512 entries). Native inventory and
content checks remain mandatory. Job logs use the exact job endpoint and raw
bytes, never trimmed console output or regenerated run-log ZIPs. Artifact names
are checked for unique current identity, so deletion/recreation fails closed.
These are execution-proof bindings; durable first-attempt campaign input context
and the canonical six-field candidate keep their existing distinct contracts.

Core-owned proof/transport modules are source-copied into the integration owner
along with verify/github before retaining a new P. The final C references P and
must prove exact raw Git blob equality for that executable closure. New wrapper
and app adoption sources follow; old receipts without the binding are not valid
under the new implementation. No unpublished anchor needs a compatibility path.
