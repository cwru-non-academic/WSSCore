# Core to Python promotion handoff

`propagate-python.yml` listens on the default branch for completion of **Build
universal WSS release**. Only a successful canonical `artifacts.yml` **push to dev**
is eligible; opening/updating a PR, workflow_dispatch, tags, failures and fork
producers do not publish Python dependency requests.

The handler independently reads the exact producer run, requires its commit in
Core dev and all eleven existing producer jobs successful, selects the unique
unexpired `universal-release-assets` artifact ID, downloads that run's artifact,
checks the archive checksum, and passes its immutable commit/run/artifact/archive/
SHA256 tuple. It never rebuilds or executes the downloaded payload.

The completion handler is a separate workflow. Its setup/dispatch failure cannot
turn the producer run red or disrupt the existing C# consumer's wait for that run.
The existing `artifacts.yml`, C# handoff and Unity handoff are unchanged.

Before requesting a write token, preparation checks the Python default branches
contain all dispatch/preflight definitions and rejects ambiguous or failed prior
correlated runs. A single pending/successful existing request is reused. An
uncertain dispatch is inspected before any retry.

## Human deployment and access

1. Publish the reviewed Python wrapper dev baseline to its default main.
2. Publish the reviewed CLI dev baseline to its default main.
3. Merge the Core handoff feature PR to its default main.
4. A subsequent genuine Core dev push with a successful producer run starts the
   flow. Do not change Core behavior/metadata merely to manufacture a trigger.

All dependency PRs target **dev**. The wrapper runs both exact-source Python candidate
preflights before a restricted dependency PR. After its HUMAN merge and normal
acceptance, the CLI runs another candidate preflight against the actual merged
wrapper and opens a gitlink-only PR. No automatic merge or release occurs.
An identical payload/gitlink is a verified no-op preserving existing provenance.

Use only the existing approved WSS App configuration, accessible to Core and both
Python repositories as `WSS_GITHUB_APP_ID` / `WSS_GITHUB_APP_PRIVATE_KEY`. Core's
dispatch token is restricted to Python-wrapper Actions-write. Consumer read tokens
cover Core/Python contents, Actions and PR metadata; preflight dispatch tokens cover
only the needed Python Actions target(s). Contents/PR-write tokens are target-only
and delayed until actual evidence validation. No credential, installation grant,
secret, security setting, ruleset or persistent permission is created here.

If destination workflows or an existing App capability are unavailable, that hop
fails with its exact setup need. Existing Core-to-C# App use does not prove installation access to either Python repository. Configuration and installation grants are not modified by this work.

Python conformance embeds CPython 3.12 and pythonnet 3.0.5 in the NUnit host, preserving the ten canonical cases and eighteen assertion expressions. Commands enter the production Python wrapper and CLI controller factory over Core's emulator/evaluator. It establishes that Python path on hosted Windows, not terminal input, GUI behavior, physical transports or every Python/runtime/platform combination.
