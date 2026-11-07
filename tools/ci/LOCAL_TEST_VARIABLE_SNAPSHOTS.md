# Local effective test-variable snapshots

This is a held implementation, not activation or hosted readiness. All test
selection policies remain OFF. The source registry is intentionally empty and
rejects enabled policies until separately reviewed receiver deployment is complete.
No new App grant, credential, repository variable or setting is required.

## Semantics and schemas

A real workflow run in each owning repository reads ONLY WSS_USE_PIN_TESTS,
WSS_PIN_TEST_REPOSITORY and WSS_PIN_TEST_COMMIT into process environment. GitHub
supplies effective org/repository configuration: repository overrides organization;
missing/empty selects latest. This does not prove repository-only origin. The
canonical job has no environment declaration. Exactly `true` pins an allowed
repository and existing lowercase full 40-character commit; false/empty/absent
selects the matching dev/main head, without fallback. Disabled pins ignore stale
pin fields. Shared refs resolve once across all targets in the existing selector.

Capture uses wss-effective-test-variable-snapshot/v1; immutable producer selection
uses wss-test-selection-artifact/v2 and retains complete request, normalized
settings, run/artifact IDs, authenticated ZIP digest and document digest. Existing
wss-test-selection-artifact/v1 remains readable as historical repository-only
capture and is never reinterpreted. Selection payload wss-test-selection/v1 and
paired plan protocols are unchanged. Recovery loads the saved producer selection;
it never reads current settings, branches or snapshot source registry again.
Ordinary contexts are now wss-ci-test-context/v2 with effective-variable semantics;
legacy v1 contexts remain readable and reusable without recapture.

## Authority and dispatch

Reviewed `test-variable-snapshot-sources.json` binds each channel/target to the
reviewed authority anchor commit, exact workflow blob, executable shared-tooling
tree, branch ref, and authenticated dispatch actor numeric ID. Preparation reads
the then-current channel head and requires its workflow/tooling identities to
match that reviewed anchor, then records the actual full head in the immutable
request. Legitimate product-only commits therefore need no registry edit; changed
workflow/tooling requires new review. The registry fields are ref, commit (reviewed
anchor), workflow, workflow_blob, tooling_tree and actor_id; the request adds
reviewed_commit and uses commit for the actual campaign source. Do not derive these fields from an untrusted request or selected
test checkout. Current source race fails before POST; actual run/source/actor,
workflow registration and blob are authenticated after dispatch. The independently
loaded capture source must also equal both paired plans' receiver snapshots after
workflow freeze/recheck and before either plan is emitted; source-only movement
with unchanged workflow bytes still fails. Request JSON in
an artifact alone proves nothing. Fork/PR/wrong actor/source/channel, duplicate
run/job/artifact, missing/expired/corrupt artifact and changed bindings fail closed.

Core persists a create-only request journal before writes, then independently
loads and authenticates its ZIP. Dispatch only runs on the canonical producer's
initial runner attempt. A fixed create-only RUNNER_TEMP marker derived from that
producer run is acquired before any POST. The immutable workflow exposes no
marker deletion or retry path. An ambiguous POST or partial dispatch stops; later
attempts never dispatch or regenerate requests. Matching runs are correlated,
not blindly repeated. This is a trusted canonical first-attempt job protocol, NOT
an arbitrary-client exactly-once API. Missing durable selection on a later
producer attempt requires inspected recovery rather than fresh settings.

Receiver reads and POST use existing narrowly scoped App Actions-write and
Contents-read capabilities; Core intent reads use the producer token. The same
existing scoped Contents-read token resolves allowed test refs centrally. Current
reviewed test-source repositories are the nine receiver repositories. Expanding
that allowlist must separately validate token installation/repository coverage;
there is no token or public-read fallback. No selected executor receives the App
key or tokens. ZIP bytes are checked against the authenticated artifact digest;
the decoder allows exactly one bounded JSON entry without extracting paths.

## Ordinary CI integration seam

A reviewed local resolver job must have no environment declaration and map exactly
these env entries before invoking ci-test-selection.mjs select:

    WSS_LOCAL_TEST_VARIABLES_SCHEMA: wss-effective-test-variables/v1
    WSS_USE_PIN_TESTS: ${{ vars.WSS_USE_PIN_TESTS }}
    WSS_PIN_TEST_REPOSITORY: ${{ vars.WSS_PIN_TEST_REPOSITORY }}
    WSS_PIN_TEST_COMMIT: ${{ vars.WSS_PIN_TEST_COMMIT }}

Pass values through env, never executable shell interpolation. The marker is an
explicit wiring contract, not authenticity by itself; immutable reviewed workflow
and existing resolver/context authority are the trust boundary. Missing mapping
fails closed. Existing frozen campaign and durable ordinary contexts do not invoke
the deferred capture callback. Family fragments are NOT activated by this change.

## Rollout order (coordinator-owned, not executed here)

1. Independently review the exact Core/common draft commit and all checks. Keep
   policies OFF. Verify protected dev/main tooling parity and existing guards.
2. After approval, retain an immutable ci/tooling/<reviewed-sha> anchor using the
   established managed process. Generate receiver files and locks through
   sync-tooling.mjs, never manual copies. The canonical module and receiver
   workflow can be adopted from that reviewed retained source before merging the
   producer-changing Core PR if current anchor/adoption guards permit it. If they
   do not, stop for coordinator sequencing, not an ad hoc bypass.
3. Prepare/review all nine receivers on dev AND main with generated ownership,
   ordinary local mapping, required family trust fixes, and original mandatory
   baseline/native gates. Register the dispatch workflow on each default branch.
   Merge in the coordinated window only, never during a frozen campaign. Do not
   freeze a new producer against a half-updated receiver fleet.
4. Independently verify reviewed receiver anchor heads, workflow blobs, executable
   shared-tooling trees, App actor ID,
   common locks and required pins; populate the reviewed source registry for both
   channels. Authority anchors cannot be predicted before receiver merges. Future
   product-only movement is observed and frozen per campaign; executable changes
   require registry review. Never weaken protected source or freeze checks.
5. Review main controller compatibility separately: main must read both historical
   selection v1 and effective v2 and retain existing plan/receipt guards. Port the
   functional controller changes through a held main PR without assuming dev/main
   product identity. Only then coordinate producer-changing Core merges and the
   resulting selection-OFF campaign. No incidental documentation/empty trigger.
6. Perform separately authorized hosted local-snapshot and select/upload/load
   proofs: exact App actor/source, actual archive transport, true/false/empty,
   matching-channel freeze, settings/branch movement, retry/replay/ambiguity and
   credential isolation. This implementation's mocks and ZIP tests do NOT prove
   App installation or receiver readiness. Only after all 18 receiver branches,
   required pins and hosted evidence pass may policy activation be proposed.
7. After repairs settle, use a genuine supported fresh campaign request if needed;
   perform the actual new classifier. Never reuse old approvals or fabricate an
   equivalence/payload change to force acceptance.

Remaining hosted proof and deployed registry values are real rollout dependencies,
not synthetic test successes. The old Variables-read diagnostic workflow is now
credential-free protocol contracts and cannot retry the denied permission probe.
