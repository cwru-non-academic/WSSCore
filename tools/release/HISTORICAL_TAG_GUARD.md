# Historical tag publisher activation guard

Removing the current tag trigger cannot change historical workflow definitions.
A newly created `v*` tag at an old commit could still invoke its old publisher.
The dev beta namespace is `dev-beta-*`, so legacy `v*` tag creation can be prohibited
while dev is validated. Main/stable tag policy is a separate later reviewed stage.

The read-only audit on 2026-10-29 found only active branch rulesets `22505518`
(dev) and `22505396` (main), with no tag rule. No settings were changed.

## Exact policy required before activation

The owner must deploy and review a repository ruleset with:

* `target: tag`, `enforcement: active`, this exact repository as source.
* `conditions.ref_name.include: ["refs/tags/v*"]`, `exclude: []`.
* A `creation` prohibition and no bypass actors. Optional update/deletion
  prohibitions are allowed; unknown parameters require another reviewed guard.

`targets.dev.json` deliberately contains unconfigured `legacy_tag_guard` fields.
Do not substitute a Boolean acknowledgement. Once the owner policy exists, inspect
it read-only with `node tools/release/cli.mjs inspect-tag-guard <ruleset-id>` and put
the returned ID and normalized policy SHA-256 into a reviewed policy PR.

`policy.mjs` verifies full live policy before freezing a plan and again before
publication. The live policy must match the reviewed fingerprint. Missing policy,
changed scope/enforcement/rules/bypasses, failed API reads or redacted bypass
metadata block publication. The accepted release record retains the verified
ruleset ID and policy digest. Setting the activation variable alone cannot pass.

GitHub restricts visibility of bypass metadata to callers with sufficient ruleset
access. Verify that the already approved hosted audit identity can read the complete
policy before activation. This implementation neither grants permissions nor adds
a privileged credential. If the existing identity cannot inspect it, report that
exact read as blocked and keep release disabled; do not assume omitted bypasses
mean none. See [GitHub's ruleset endpoint documentation](https://docs.github.com/en/rest/repos/rules#get-a-repository-ruleset).

If full live inspection cannot be supported with the approved permissions, the
alternative is a separately reviewed producer workflow-path migration followed by
disabling the old workflow identity across historical refs. That requires updating
every receiver's strict producer-path contract first. Do not disable the current
producer or relax its provenance checks to improvise around this gate.

Repository administrators can change policy or publish manually; their powers
cannot be removed by an Actions-only verifier. No release, tag push, settings,
ruleset, secret or permission change is performed by this preparation.
