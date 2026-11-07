# Credential-isolated selected-test executor handoff

This is an opt-in Core API contract, not deployed receiver wiring, readiness proof,
policy activation, or production acceptance. The existing `select`, `load`,
`inspect`, and `evidence` commands retain their behavior. In particular, legacy
campaign inspection still authenticates the plan remotely and must not be used
in an executor without that authority.

## Existing route and the missing boundary

`select` prepares/reuses the durable first-attempt context. The reviewed workflow
uploads it once, with overwrite disabled. `load` authenticates the run, immutable
workflow source, exact input policy/mode/candidate, unique unexpired first-attempt
artifact, and its envelope. Campaign verification then calls the existing
`loadPlan`: canonical Core artifacts.yml push producer, plan digest, exact reviewed
producer policy, candidate, and frozen selection. These rules are unchanged.

The old returned context is not sufficient for an offline campaign check: both
`inspect` and `evidence` call `loadCampaignTestBinding` again. A selection ID alone
is not plan authentication. Passing source tokens or the App key to that job,
changing a campaign to run scope, skipping validation, using fixture IDs, or
calling a separate readiness protocol does not resolve this boundary.

## Trust model

The trust anchor is a **reviewed prerequisite job output in the same workflow**,
not a digest printed by the downloaded file. GitHub supports passing producer job
outputs to dependent jobs through `needs.<job>.outputs`:
https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/pass-job-outputs

The trusted job must execute only reviewed immutable bootstrap/tooling code. It
must finish before any selected code executes, including in parallel jobs. Its
job output must map directly from the successful `load-executor` step. It must
not incorporate outputs from selected code, a dispatch input, a downloaded
self-declared checksum, mutable branch contents, or another workflow. A digest
cannot authenticate an arbitrary job or a malicious workflow definition.
Existing canonical receiver workflow/source freeze and release collection gates
remain the authority for production. PR workflows require their existing
untrusted-source/secret restrictions; this contract does not authorize secrets
for an untrusted PR or turn a PR result into release evidence.

A separate fresh runner job is required for selected execution. No App action,
App private key, source token, saved gh authentication, credential-bearing home,
Git config, environment, cache, or action state may cross into it. Checkouts must
not persist credentials. Review the actual job permissions and all actions,
including implicit github.token use. GitHub documents the risk of shared files,
processes, containers, and insufficiently isolated runners:
https://docs.github.com/en/actions/reference/security/secure-use

The handoff does not assert that GitHub's own artifact service/runner capability
is absent, nor does it sandbox selected code. The known-credential environment
check is defense in depth, not proof that an arbitrary runner is clean. Existing
whole-Core, immutable-product and external-Git-authority guards remain required.
In particular, HEAD plus clean Git status is insufficient: inherited clean/smudge
filters and replacement refs can hide different executable bytes. Callers must
authenticate the literal reviewed bootstrap/tree bytes with replacement objects
disabled and must close inherited Git/filter/index/environment authority. This
API adds no new Git-based bootstrap verifier and cannot repair those family
guards by hashing a handoff. Their independent hardening is an adoption dependency.
A persistent self-hosted runner shared with the auth job does not satisfy this
contract merely because the YAML uses two job IDs.

## Authenticated producer API

Use the existing `select` and create-only durable context upload. Then replace
only the prerequisite's final `load` with:

    node <trusted-core>/tools/ci/ci-test-selection.mjs load-executor <target> <candidate.json|-> <new-output-dir> <committed|candidate>

This still performs all remote `load` and campaign checks. It writes exactly
`executor-handoff.json` using create-only semantics, then exports:

- `executor_handoff_sha256`: SHA-256 of Core's canonical JSON handoff.
- The existing `scope`, `selection_id`, `target_id`, `test_repository`, and
  `test_commit` coordinates, after authentication.

The closed handoff contains current run/attempt/ref/workflow/source identity,
reviewed local target policy, actual complete candidate identity (or null),
durable context artifact identity and context digest, a sanitized verified test
binding, and campaign producer/plan ID/policy digest when applicable. It never
serializes process.env, a GitHub client, raw API state, token action outputs, or
raw unverified context fields. Campaign scope stays campaign scope. Both legacy
v1 and effective-local-variable v2 durable contexts remain recoverable.

Upload only the handoff as a separate create-only artifact with an attempt-specific
name such as `wss-ci-test-executor-<target>-a<attempt>`. Retain the returned artifact
ID as another trusted prerequisite output and download that exact artifact ID in
the executor within the same run. Do not use a mutable name alone as authenticity,
and never mix the handoff with final `wss-selected-tests-...` evidence.

GitHub's v4 artifacts are immutable after creation, but deletion/recreation and
wrong-artifact selection still need fail-closed handling. The download action's
own digest mismatch is documented as a warning, not this contract's hard gate:
https://docs.github.com/en/actions/tutorials/store-and-share-data

A transport swap, extra JSON field, foreign handoff, or changed byte content
that changes canonical JSON fails Core's independently supplied digest check.
Whitespace/key ordering is intentionally insignificant; duplicate JSON member
spellings parse to their effective value and cannot change the bound object.
Do not execute scripts or read executable instructions from the handoff archive.

## Credential-isolated consumer API

The reviewed workflow maps these environment values directly from its trusted
prerequisite's job outputs, with no script-generated intermediate authority:

    WSS_TEST_EXECUTOR_HANDOFF_SHA256: ${{ needs.selected-test-prerequisite.outputs.executor_handoff_sha256 }}
    WSS_TEST_SELECTION_ID: ${{ needs.selected-test-prerequisite.outputs.selection_id }}

The producer-side output mapping must be equally direct. This is an illustrative
review contract, not a complete workflow or authorization to activate a fragment:

    jobs:
      selected-test-prerequisite:
        outputs:
          executor_handoff_sha256: ${{ steps.authenticated_load.outputs.executor_handoff_sha256 }}
          selection_id: ${{ steps.authenticated_load.outputs.selection_id }}
          executor_artifact_id: ${{ steps.handoff_upload.outputs.artifact-id }}
        # authenticated_load runs only the exact reviewed Core load-executor.
        # handoff_upload uploads only its executor-handoff.json, overwrite:false.
      selected-executor:
        needs: [selected-test-prerequisite, mandatory-family-gates]
        # Reject failed/skipped mandatory jobs; do not invent a placeholder gate.
        # Download the exact needs.selected-test-prerequisite.outputs.executor_artifact_id.
        # Map the two env values above directly, before trusted offline inspection.
        # This job has no App action, source token, App key or bootstrap credential state.

Keep real GitHub runner identity variables unchanged. Set WSS_CI_CHANNEL from the
existing verified dev/main resolver. Fetch immutable source inputs without
passing bootstrap credentials to selected execution. Then use:

    node <trusted-core>/tools/ci/ci-test-selection.mjs inspect-executor <target> <executor-handoff.json> <roots.json>
    node <trusted-core>/tools/ci/ci-test-selection.mjs evidence-executor <target> <executor-handoff.json> <roots.json> <evidence.json>

These explicit commands never construct a GitHub client, read gh authentication,
call an API, reload the plan, read variables, or resolve refs. They validate the
trusted digest, strict shape, exact source/run/current attempt/workflow/channel,
policy, candidate, selection, and source checkout gates. They reject known
bootstrap credential environment variables. There is no fallback to weaker
ordinary validation if any check fails. The generic final evidence is unchanged.

All nine family adapters can call the same API. Each adapter must retain the
trusted handoff digest and source authority across selected child execution,
recheck original roots/Core/Git metadata, retain all mandatory baseline/native/
profile/architecture/projection jobs, and publish final evidence only after the
complete required matrix succeeds. A digest taken from the selected child's
output after it runs is not the trusted prerequisite output. Failure or skipping
any required stage must suppress final publication. Family adapters and workflow
DAGs need separate independent review; no fragment is activated by this API.

## Recovery and replay

The durable selection is still the original first-attempt artifact, never a new
resolution on retry. On attempt 2+, rerun the trusted auth job and dependents.
`load-executor` reauthenticates that same selection and the canonical campaign,
then emits a new handoff for the current attempt. Earlier-attempt handoffs and
stale successful-job outputs are rejected. GitHub's rerun-failed-jobs flow is
therefore insufficient if it does not rerun the auth prerequisite. Missing,
expired, duplicate, foreign, late, or malformed durable context fails closed.

The handoff is not a reusable bearer grant, cross-workflow attestation, or
long-term substitute for canonical plan authentication. Recovery after outputs
are lost must return to the authenticated loader. It neither regenerates an
expired campaign nor authorizes reuse of a retired frozen receiver plan.

## Scope and validation

No receiver workflow, retained source anchor, pin, activation JSON, policy,
registry, App grant, repository variable, payload, source gitlink, or settings
change is part of this patch. Readiness schemas and their empty/closed registry
remain disjoint. The final collector continues authenticating the campaign and
actual run/evidence with all existing release rules.

Actual child-CLI tests cover old ordinary and campaign paths plus the opt-in
handoff, first-attempt and retry recovery, unchanged evidence, fail-fast API
sentinels in the executor, token/key non-export, changed/foreign/replayed values,
missing trusted outputs, secret-bearing executor environments, and both-way
legacy/handoff misuse. Pure contract negatives cover unknown fields, malformed
IDs, readiness substitution, policy/workflow/source drift, changed selection,
and additional credential fields. Synthetic IDs are local fixtures only.

Before deployment, independently review this contract and its caller DAG, then
obtain real hosted auth/upload/job-output/download/offline execution/retry proof
under the existing authorized rollout. No synthetic local test proves real
GitHub job isolation, hosted authentication, or production selected acceptance.
