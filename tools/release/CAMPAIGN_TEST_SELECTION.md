# Opt-in frozen campaign test selection

This extension is inactive in every checked-in live policy. Producer preparation,
durable upload, paired freeze, and the consumer bootstrap are implemented behind
that policy. Product pins, adapter guards, installed App permissions and repository
variable values are unchanged. Old plans keep their existing checks.

## Reviewed contract

Both acceptance and staging policies must carry the same `test_selection_policy`:
`{schema: "wss-campaign-test-selection/v1", targets: [...]}`. The nine entries
have exactly `id`, `repository`, `default_test_repository`, and
`allowed_test_repositories`. Target identities match the reviewed acceptance
manifest and staging family destinations. The repository identifies the owner
of the executable test bundle, not the product source or merely its fixtures.

`prepareProducerTestSelection` in `campaign-selection-artifact.mjs` authenticates
this exact running Core push producer, immutable policies, and runner attempt.
It reuses the unique unexpired `wss-test-selection` artifact if present. Only a
first attempt with no artifact may invoke the injected repository snapshot
reader and resolve refs. API failures, expired/duplicate/corrupt artifacts, and
absence on later attempts fail closed without reading variables or test refs.

Upload `result.document` as the sole `selection.json` file only when
`result.created` is true. This versioned envelope binds the canonical producer,
creating attempt 1, and full selection. Then independently call
`loadProducerTestSelection` to authenticate and read the uploaded artifact.
Pass its return value as `testSelection` into `freezeCampaign` before either
plan freezes. `freezeCampaign` never resolves fresh variables or refs itself.
No initial handoff may occur until durable upload and both freezes succeed.

`resolveCampaignTestSelection` also offers create-only local lock reuse for
library/offline callers. Local lock existence is not authenticated cross-run
persistence and is not a substitute for the producer artifact flow above.

Both plans embed the full `test_selection` and `test_selection_id`. New-policy
plans missing either fail closed, including after their plan hash is recomputed.
Paired freezes reject different policies or selection bytes. Immutable producer
policy comparison prevents removing the opt-in field to select legacy behavior.

## Receiver and collector contract

After authenticated `loadPlan`/`loadStage`, `planTestSelection(plan, kind)` returns
`{selection, request, selection_id}` or null for legacy policy.
`targetTestEvidence(plan, targetId)` returns the existing five-field evidence
shape. It is an expected descriptor, not proof that anything ran.

The trusted normal/preflight workflow must check out and execute that exact test
source while preserving the independent product source and all existing checks.
Only after successful execution may it upload `evidence.json` in
`wss-selected-tests-<target-id>-a<run-attempt>`. The five fields are `selection_id`,
`target_id`, `repository`, `test_repository`, and `test_commit`.

Collectors independently download the unique unexpired artifact from the
already authenticated normal/preflight run, verify artifact run/head/attempt
identity, and compare its parsed bytes to the frozen selection. A receipt's
`selected_tests` declaration alone is insufficient. It must match authenticated
normal evidence; each preflight supplies its own authenticated artifact. Stage
fanout also requires the wrapper's normal selected-test artifact. Existing
producer, product ZIP, workflow, jobs, human merge, parent links, coverage,
receipt bytes, and publication guards remain mandatory.

## Remaining adoption steps

1. Deploy reviewed receiver bootstraps, test bundles, artifact upload, and receipt
   declaration support across all nine targets and required branch baselines.
2. Preserve normal/preflight product roots and existing adapter-pins checks;
   adopt stable bootstrap tooling on both branches and review literal pin updates.
3. Verify the installed App already grants repository Variables read access for
   the required targets before activation. The implemented reader paginates at
   thirty, validates complete unique results, and reads repository variables only.
   Organization/environment inheritance is not used. A denied read is an error,
   never an unset pin. Any installation permission expansion is a separate action.
4. Verify the implemented producer prepare/upload/independent-load sequence in
   the real campaign. Never overwrite or delete an existing selection artifact
   during retry recovery.
5. Review identical policy allowlists in both producer policy files, exercise
   actual normal/preflight/receipt artifacts, then opt in. Do not activate policy
   merely because these dormant library contracts pass.

## Consumer bootstrap operation

Receivers check out the full reviewed Core tooling tree at the immutable SHA in
its verified tooling lock. These CLI files use Core's directory layout; do not
copy only individual scripts into a receiver and assume their imports work.

The trusted prerequisite calls `tools/ci/ci-test-selection.mjs select <target-id>
<candidate.json|-> <output-directory> [committed|candidate]`. It passes the actual
GitHub run identity, explicit `WSS_CI_CHANNEL`, read-only source/plan `GH_TOKEN`,
and separate repository-only `WSS_TEST_VARIABLE_READ_TOKEN`. The helper reads
current repository settings only for a fresh ordinary run selection. Candidate
or current-product runs under an enabled policy use the durable campaign lock.
PR runs use an independent snapshot of the matching branch tests.

Upload `context.json` once in the prerequisite, pass its `selection_id`,
`test_repository` and `test_commit` outputs to dependent jobs, and download that
same context there. Check out the selected test commit separately without
submodules or persisted credentials. Keep `WSS_TEST_SELECTION_ID` tied to the
prerequisite output, never a value read from selected test code.

Before execution call `inspect <target-id> <context.json> <roots.json>`. After all
required fixed baseline and selected tests succeed, call `evidence` with the same
arguments and a new output file. The roots file contains `selection_id`,
`test_root`, and `products` entries with repository, full commit and checkout path.
Include the actual target source and every initialized product submodule; each
gets independent commit, cleanliness, index-flag and executable-cache checks.
Selected test gitlinks must remain uninitialized and use explicitly frozen external
dependency checkouts instead. No test and product checkout may overlap.

Upload only the resulting `evidence.json` as
`wss-selected-tests-<target-id>-a<run-attempt>`. This upload is additional to all
existing rich runtime, projection, fixture, platform and mandatory baseline
artifacts. It never replaces them. Keep test execution steps free of App tokens
and publishing credentials; source/plan tokens belong only to trusted bootstrap
steps.
