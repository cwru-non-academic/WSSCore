# Repository-variable test selection

## Status and scope

This PR adds a tested selector and immutable test-source/evidence contract. It is
**not activated in receiver workflows**. It does not modify repository variables,
product gitlinks, native payloads, release policy, existing executable workflow
pins, `adapter-pins.mjs`, receipts or existing frozen plans. The adoption steps
below are required before repository settings affect actual CI.

## Requested behavior

Repository variables belong to the repository whose tests are being selected:

| Variable | Meaning |
| --- | --- |
| `WSS_USE_PIN_TESTS` | Exactly `true` enables the pin. `false`, empty or absent selects latest. Other values fail rather than silently changing policy. |
| `WSS_PIN_TEST_REPOSITORY` | Required when enabled. An `owner/repository` from the reviewed test-source allowlist. |
| `WSS_PIN_TEST_COMMIT` | Required when enabled. A full 40-character lowercase commit SHA present in that repository. Branches, tags and abbreviated SHAs are rejected. |

A disabled pin ignores stale repository/commit variables. With latest selected,
a dev campaign resolves the reviewed default test repository's dev head; main
resolves its main head. There is no main fallback for a missing dev branch.
Missing repositories, inaccessible commits and malformed enabled pins fail closed.
None of these variables disables testing.

The trusted request supplies each target's default test repository and allowed
pin repositories. Repository variables cannot invent executable-source trust or
change the product being tested. Extending that allowlist is a reviewed code
change. The campaign controller must obtain effective variable snapshots from the
owning repositories, not reuse the Core repository's `vars` for every receiver.
Reading repository variables through the GitHub API requires the relevant existing
API permission; this PR does not create credentials or expand app permissions.

## Resolve once and reuse

`tools/ci/test-selection.mjs` exports:

- `resolveTestSelection(request, github)`: creates a selection for a campaign. Each
  distinct repository/ref is resolved once even when several targets use it.
- `resolveOrReuseTestSelection(request, github, path)`: create-only local output.
  An existing valid selection is reused without rereading settings or resolving
  branch heads. A corrupt or wrong-campaign file is an error, not permission to
  replace the selection. Concurrent creators keep the first valid complete file.
- `verifyTestSelection(selection, request, expectedSelectionId)`: authenticates
  the selected sources against the ID obtained from the verified frozen plan.
- `verifySelectedTestEvidence(...)`: requires the exact source, target and
  selection identity in evidence. This is an additional assertion, not a release
  eligibility decision or proof that the reported source actually executed.

The request has exactly `channel`, `campaign` and `targets`. `campaign` identifies
an exact repository, full commit SHA and producer run ID. Each target declares
`id`, `repository`, `default_test_repository`, `allowed_test_repositories` and a
`variables` object containing only the three settings above. All nine required
release targets remain the responsibility of the existing release policy.

Example request for one target (illustrative SHAs, not deployable sources):

```json
{
  "channel": "dev",
  "campaign": {
    "repository": "cwru-non-academic/WSSCore",
    "commit": "1111111111111111111111111111111111111111",
    "run_id": "42"
  },
  "targets": [{
    "id": "python-wrapper",
    "repository": "cwru-non-academic/Python_WSS_Integration_Lib",
    "default_test_repository": "cwru-non-academic/CSharp_WSS_Integration_Lib",
    "allowed_test_repositories": ["cwru-non-academic/CSharp_WSS_Integration_Lib"],
    "variables": {"WSS_USE_PIN_TESTS": "false"}
  }]
}
```

Run `node tools/ci/test-selection.mjs resolve request.json selection.json` at
campaign creation. The result records each selected full SHA and hashes the
complete channel, campaign and target selection as `selection_id`. Commit this
identity into the existing frozen campaign plan before dispatch. Persist the
selection as an artifact from that exact verified producer run. Downstream jobs,
retries, candidate validation, normal CI and receipts must reuse it; they must not
resolve latest or inspect current pin settings again. Settings and branch movement
affect only the next campaign. A new campaign resolves a fresh selection.

A self-reported digest is not authentication. Receivers must verify the producer,
run, artifact and plan with the existing proof chain, then pass the selection ID
from that verified plan. Do not accept a selection ID from the downloaded selection
itself as the authority. Local create-only output also requires a stable campaign
identity and must be authenticated after transfer.

## GitHub Actions implementation constraint

GitHub reusable workflow `jobs.<id>.uses` requires a literal workflow reference;
expressions such as `owner/repo/.github/workflows/tests.yml@${{ vars.REF }}` are not
supported. Do not replace existing immutable reusable pins with that syntax or a
floating branch.

Keep a small, reviewed reusable workflow pinned to a literal full SHA. Give it the
verified selection ID and source coordinates as inputs. In runner steps, check out
the selected **tests** at the frozen SHA into a separate directory. Those test
scripts/fixtures execute against the original selected product checkout and exact
candidate projection. Dynamic checkout inputs are supported; dynamic reusable
workflow selection is not. Nested reusable workflows remain part of the literal
bootstrap revision, so selecting tests cannot secretly switch workflow logic.

Test checkouts must use read-only credentials, `persist-credentials: false` and
separate paths. Never make the selected test repository the release authority or
expose publishing tokens to its code. A script-driven test entry point is needed
where tests currently exist only as YAML steps. Test configuration and fixtures
must be versioned with their selected source. Product configuration, source SHAs,
SDK/runtime identity, canonical case inventory and producer provenance remain
independently checked; do not copy new product sources or substitute DLLs just to
make new tests pass.

## Required adoption and rollout order

1. Review explicit test-source ownership and allowed repositories for each target.
   Separate test scripts/fixtures from product sources and trusted release tooling.
   Preserve ordinary committed-state coverage and candidate coverage separately.
   Latest tests are allowed to expose old-product incompatibility; do not suppress
   required cases or silently fall back to older tests.
2. Add a frozen selection field to the campaign plan schema, validation and digest.
   Wire effective repository variable snapshots into campaign creation. Add
   authenticated transport and exact selection inputs to every required receiver,
   normal-CI run and preflight. Record selected test identity alongside actual
   product and projection identity in evidence.
3. Add selection verification to each applicable receipt/evidence collector.
   Reject missing, changed, foreign-campaign or substituted test source proof.
   Preserve producer, archive hash, workflow tree, normal CI, actual merge,
   product gitlink, all-target, approval and release gates without exceptions.
4. Deploy reviewed literal workflow bootstraps and receiver changes through PRs.
   Verify full hosted coverage at each final commit on dev, then normal dev-to-main
   promotion. No `uses` expression, floating executable pin or live branch recheck
   is an acceptable substitute for the frozen test selection.
5. Only after the receiver tooling has settled, create a fresh supported producer
   and frozen campaign. Never edit an existing plan or combine receipts from old
   and new campaigns. Then verify a complete new campaign before enabling any
   publication gate.

### Existing blocker remains explicit

`tools/ci/adapter-pins.mjs` currently requires an executable integration pin's
script/config/workflow objects to match **both** live dev and main. New wrapper
test code can diverge between those branches. This PR intentionally does not relax
that release-tool identity rule, classify trusted validators as test fixtures, or
make repository variables override it. If separating test-only entry points is
not sufficient, changing that guard to reviewed immutable tooling locks requires
a separately reviewed design and approval. Test selection is not that approval.

Existing frozen campaigns also retain old workflow-tree snapshots. Recent
acceptance failures for `csharp-wrapper:dev` / `csharp-wrapper:accepted-baseline`
therefore cannot be fixed by rerunning those old campaigns after workflow changes.
A fresh producer and frozen campaign is required after adoption.

## Validation

`node --test tools/ci/test-selection.test.mjs` covers pin/latest semantics, dev/main
selection, missing/malformed pin settings, existence checks, shared-source caching,
request immutability, campaign/channel binding, altered artifact/evidence rejection,
create-only reuse and disabled stale settings. The normal Common CI contracts
workflow runs these tests on Linux, Windows and macOS. Existing guard tests remain
unchanged and must continue passing.
