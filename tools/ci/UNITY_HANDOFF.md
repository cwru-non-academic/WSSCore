# Core to Unity promotion handoff

`propagate-unity.yml` listens on the default branch for completion of **Build
universal WSS release**. Only a successful canonical `artifacts.yml` **push to dev**
is eligible; opening/updating a PR, workflow_dispatch, tags, failures and fork
producers do not publish Unity dependency requests.

The handler independently reads the exact producer run, requires its commit in
Core dev and all fourteen required producer jobs successful (including the universal macOS build and both macOS BLE consumers), selects the unique
unexpired `universal-release-assets` artifact ID, downloads that run's artifact,
checks the archive checksum, and passes its immutable commit/run/artifact/archive/
SHA256 tuple. It never rebuilds or executes the downloaded payload.

The completion handler is a separate workflow. Its setup/dispatch failure cannot
turn the producer run red or disrupt the existing C# consumer's wait for that run.
The immutable producer tuple and C# dispatch contract are preserved. The macOS BLE extension adds native aggregation and macOS gates; see `MACOS_BLE_ROLLOUT.md` for receiver prerequisites.

Before requesting a write token, preparation checks the Unity default branches
contain all dispatch/preflight definitions and rejects ambiguous or failed prior
correlated runs. A single pending/successful existing request is reused. An
uncertain dispatch is inspected before any retry.

## Human deployment and access

1. Publish the reviewed Unity wrapper dev baseline to its default main.
2. Publish the reviewed GUI dev baseline to its default main.
3. Merge the Core handoff feature PR to its default main.
4. A subsequent genuine Core dev push with a successful producer run starts the
   flow. Do not change Core behavior/metadata merely to manufacture a trigger.

All dependency PRs target **dev**. The wrapper runs both exact-source .NET candidate
preflights before a restricted dependency PR. After its HUMAN merge and normal
acceptance, the GUI runs another candidate preflight against the actual merged
wrapper and opens a gitlink-only PR. No automatic merge or player release occurs.
An identical payload/gitlink is a verified no-op preserving existing provenance.

Use only the existing approved WSS App configuration, accessible to Core and both
Unity repositories as `WSS_GITHUB_APP_ID` / `WSS_GITHUB_APP_PRIVATE_KEY`. Core's
dispatch token is restricted to Unity-wrapper Actions-write. Consumer read tokens
cover Core/Unity contents, Actions and PR metadata; preflight dispatch tokens cover
only the needed Unity Actions target(s). Contents/PR-write tokens are target-only
and delayed until actual evidence validation. No credential, installation grant,
secret, security setting, ruleset or persistent permission is created here.

If destination workflows or an existing App capability are unavailable, that hop
fails with its exact setup need. Existing Core-to-C# App use is observed, but does
not prove installation access to either Unity repository. Repository-level name
inventories are empty; inherited organization configuration is not established by
that inventory. Do not request broader account scopes to inspect it automatically.

Ordinary .NET conformance reuses the existing ten cases/eighteen assertion
expressions through shared production commands and the Core emulator/evaluator.
It does not establish Unity import/runtime, scene/UI, player or physical-device
compatibility; the separate manual Editor harness remains available.
