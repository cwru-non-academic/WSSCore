# Opt-in frozen campaign test selection

This extension is inactive in every checked-in live policy. It does not change
product pins, adapter tooling guards, receiver workflows, app permissions, or
repository variables. Old plans keep their existing checks.

## Reviewed contract

Both acceptance and staging policies must carry the same `test_selection_policy`:
`{schema: "wss-campaign-test-selection/v1", targets: [...]}`. The nine entries
have exactly `id`, `repository`, `default_test_repository`, and
`allowed_test_repositories`. Target identities match the reviewed acceptance
manifest and staging family destinations. The repository identifies the owner
of the executable test bundle, not the product source or merely its fixtures.

`freezeCampaign` in `campaign-freeze.mjs` obtains one selection before freezing
either plan. Its injected `resolveSnapshots(targets)` returns a complete map of
target IDs to the three repository-variable values. A failed/unauthorized read
must throw, never be represented as unset. No implicit cross-repository variable
access or org/environment inheritance is supplied by this module.

`selectionPath` is a create-only durable lock. Reuse checks campaign identity,
policy, and digest without querying variables or moving refs. Controllers must
persist and authenticate that lock across runner attempts before calling this
API; a fresh runner path is not cross-run persistence. Never reconstruct a
missing lock for an existing campaign from current variables or branch tips.
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
3. Verify a least-privilege repository-variable reader and connect it through
   `resolveSnapshots`; do not expand permissions implicitly or trust a caller's
   unverified snapshot file. The current CLI intentionally has no active reader.
4. Add authenticated cross-attempt lock persistence/recovery and pass its path
   into the paired freeze controller. First-run creation and retry recovery must
   distinguish absence from an inaccessible or expired previous artifact.
5. Review identical policy allowlists in both producer policy files, exercise
   actual normal/preflight/receipt artifacts, then opt in. Do not activate policy
   merely because these dormant library contracts pass.
