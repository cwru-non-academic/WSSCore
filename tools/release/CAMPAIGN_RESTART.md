# Explicit fresh-campaign restart

A restart is a reviewed control-state change, not a rerun of an old producer.
The operator prepares one immutable request record and submits it through the
ordinary protected-branch PR process. Merging it starts the existing canonical
`push` producer. No receiver accepts a new event type or a manual producer.

## Why this mechanism

An Actions rerun retains the producer run ID and artifact namespace. It cannot
replace frozen plans or refresh selected-test settings safely. A dispatch-based
producer would require a coordinated trust-contract rollout across all pinned
receivers. This mechanism instead changes an explicit campaign-request record,
leaving the entire product/tooling tree unchanged from its named parent. The
new push gets its own run ID, archive artifact ID, plans, and receipt namespace.
Artifact names remain the existing fixed names **within different runs**; no
artifact is overwritten, deleted, or duplicated in an existing run.

## Deployment and authority

Deploy this implementation in its own reviewed tooling PR before preparing any
request. Its merge to dev is itself a normal producer trigger and may already
provide the needed new campaign. Inspect that producer first; do not create a
redundant restart. The CLI does not need a default-branch dispatch workflow, so
main deployment is not required to restart dev. A main tooling merge also starts
a main producer. Do not perform that merge before the actual main product/source
adoption and existing main gates are ready.

The fixed request schema is `wss-campaign-restart/v1`. It binds:

- Channel and exact expected current Core commit.
- A successful canonical predecessor candidate, acceptance plan ID, and stage
  plan ID. Required producer jobs, immutable source policies, archive ID/bytes,
  paired selection, and main pair linkage are independently authenticated.
- Both dev and main heads of all nine receiver repositories.
- A nonempty explicit operator reason and SHA-256 request ID over the body.

Records live at `tools/ci/campaign-restarts/<channel>/<request_id>.json`.
They are append-only. Cross-channel history promotion may import a record only
if its exact blob already exists unchanged on the originating channel. This
allows later dev-to-main adoption without interpreting a historical dev request
as a new main request. The destination channel must already have this validator
before importing request history; deploy tooling separately rather than mixing
tooling introduction and request-history import into the same PR.

A request PR uses the validator from the deployed base commit, not a validator
edited by that PR. The required workflow is still part of the reviewed source;
this is not an external, independently tamper-proof branch-protection service.
Existing protected workflow review is the trust root. No DCO sign-off, digital
signature, or new attestation is made on anyone's behalf.

## Prepare and review

Use a dedicated clean branch at the exact live channel head, with ordinary
operator read access through `gh`. Supply the exact successful predecessor run
and explain which receiver changes require a new freeze:

```
node tools/release/campaign-restart-cli.mjs prepare dev <run-id> <40-character-current-Core-SHA> "Receiver tooling changed after the previous freeze"
```

The command authenticates existing evidence and writes only the request JSON.
It does not create a branch, commit, push, PR, merge, dispatch, token, repository
variable, tag, or release. Review the file, commit only that file, and open a
request-only PR against the named channel using the normal reviewed process.
Reuse the same branch/PR if preparation is repeated: identical inputs produce
the same request ID and an existing identical file is left unchanged. The CLI
will not overwrite a different file or accept unrelated branch/worktree edits.

Prefer a one-commit request PR. Merge or squash is supported. A one-commit
rebase is supported only if the resulting push directly extends the named
expected base. Multi-commit rebase requests are rejected if they place the
record before a different final parent. Never add incidental documentation,
source changes, settings changes, or an empty commit to force a restart.

Core conformance validates request changes in PR checks. The canonical push
revalidates the whole `event.before` to new-head range, requires the request's
expected head to be the direct first parent, and requires the sole tree change
to be the added request record. It checks live Core/receiver heads before
freezing and checks the new paired plans against the requested receiver heads
before uploading plans or acquiring downstream write credentials. Existing
workflow-tree and current-head guards remain active at the actual dispatch.

## Races, retries, and limits

This is **not merge-time compare-and-swap**. Strict up-to-date PR checks reduce
base races, but a concurrent base/receiver change can still make a merged
request fail closed. In that case no downstream handoff is authorized. Inspect
the failed run; do not overwrite the request, reuse old receipts, or turn a
manual workflow run into a canonical producer. To recover from a failed request-only checkpoint, prepare a new request on a
new dedicated branch at that **new current head**, explicitly supplying the last
successful predecessor run. The validator proves that predecessor is an ancestor,
reads complete non-truncated Git trees, and requires identical product/tooling/
policy file bytes, modes, and submodule pins. All prior request records must be
unchanged; additional records must be valid regular request files. It then takes
fresh receiver snapshots. If any real source/policy differs, this historical
recovery is rejected: fix the actual source failure through a reviewed code PR
and obtain a successful canonical producer first. Never fabricate success or
relax the normal current-head guards.

Once merged, the record is historical evidence and cannot be modified/deleted
or re-added as a new request. A new deliberate restart needs a proven successful predecessor (current-head or
request-only ancestry), fresh receiver snapshot, and a different request ID.
Ordinary reruns retain their original run ID and all existing artifact
immutability rules. This feature does not repair duplicate/missing artifacts or
make a full producer rerun safe when immutable artifacts already exist.

With selected tests enabled, the new producer alone resolves and persists its
selection once on attempt 1. Its retries reuse that authenticated selection;
they never reread settings as a fresh campaign. Required repository-variable
read access must already exist. This feature neither enables selection nor
requests/changes App permissions. With selection disabled it needs no variable
reads. Publication remains gated by all existing controls and accepted receipts;
a restart request is not permission to publish.

Both CLI and producer checks use existing read access. No App installation,
repository protection, token grant, or repository-variable setting is changed.
The task that adds this mechanism must not prepare a real request or merge it
merely to test the feature. Use fixtures and the implementation PR's normal CI.
