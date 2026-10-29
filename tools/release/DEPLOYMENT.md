# Dev beta release deployment gates

This implementation is a disabled preparation, not evidence of an all-clear or a
release. The initial contract-only Core PR17 was merged at `4484d1c`; implementation
changes are reviewed separately. No main/stable channel is enabled.

## Before activation

1. Review and merge the Core verifier and seven receiver receipt writers. Synchronize
   this **dev-only tooling** through reviewed default/main and dev PRs; registering
   the workflows on the default branch is required by GitHub. This is not stable
   dependency propagation. Frozen receipt scripts and normal workflow object IDs
   must match at the accepted merge. C# writers verify their main checkout against
   these exact IDs before executing shared validators.
2. Each required target's normal workflow must support a no-input `workflow_dispatch`
   against dev and test the exact dispatched commit. All seven live required job
   lists were read and matched on 2026-10-29. Require fresh postmerge runs; a PR run's
   API head SHA does not prove its synthetic merge checkout.
3. Complete and test unchanged-payload equivalence receipts before using no-op
   candidates. Current code deliberately rejects no-op acceptance rather than
   relabeling an old approval. Human migration PRs cannot count as bot dependency
   acceptance. A candidate with no payload change therefore cannot currently release.
   `NOOP_V2.md` defines the migration interface; `equivalence.mjs` tests its byte and
   source foundations without enabling v2 acceptance in the v1 publisher.
4. Close the historical tag publisher route through repository policy **before**
   enabling publication. Removing `v*` and `publish-universal` from current YAML
   prevents that route for new commits only. A newly pushed tag on an old commit
   can execute its historical YAML. The repository owner must prohibit unapproved
   legacy `v*` tag creation (and review any other release writers). This PR does not
   change rulesets, secrets or permissions. Existing manual administrator powers
   are outside an Actions evidence barrier.
   `HISTORICAL_TAG_GUARD.md` specifies the exact owner policy and API visibility
   requirement. The executable guard rechecks the pinned policy before freezing
   and publication; unconfigured guard fields deliberately block activation.
5. Validate the exact policy and emitter interface with all receiver owners,
   including canonical paired preflights, source-scope/projection validators and
   current gitlinks. Unity coverage remains the hosted .NET compatibility harness;
   it does not establish macOS Unity Editor or Player BLE runtime support.
6. Validate a complete dry-run plan, failed/missing/ambiguous/stale proof rejection,
   release replay and inspected uncertain-dispatch recovery. Then explicitly enable
   `WSS_DEV_RELEASE_ENABLED=true` using the owner's existing configuration process.
   This preparation never sets that variable.

## Routine sequence after deployment

Successful canonical dev push -> immutable two-file universal artifact -> separate
frozen seven-target plan -> existing dependency PR graph -> human merges -> fresh
normal CI at actual dev merge SHAs -> trusted receipt runs -> independent all-target
verification -> `dev-beta-<producer-run>-<plan-id-prefix>` prerelease containing the
original `WSS-Universal-ci.zip`, checksum, frozen plan and acceptance record.

Reconciliation requests only existing normal CI and receipt workflows. It waits
when a receiver has not moved from its frozen starting head, reuses pending/success
runs and stops on failed or ambiguous proof. It does not create dependency PRs,
merge, rollback or rewrite branches. An uncertain dispatch must be inspected before
retry; a successful HTTP dispatch is not a receipt. API dispatch has no atomic
cross-repository idempotency key, so uncertain-outcome journaling/recovery remains
an activation prerequisite. Do not silently retry a failed or unconfirmed dispatch.

An existing release is reused only when its tag commit, prerelease state, exact
asset inventory and every downloaded asset digest match. A partial or different
release blocks for inspection; the publisher never overwrites or deletes assets.
The ZIP is downloaded and hashed again, never rebuilt or repacked for publication.

MATLAB is excluded from dispatch and required acceptance in the versioned policy.
Main-to-main stable propagation and publication are a second reviewed stage only
after this dev system is validated. The held larger Core candidate is not involved.
