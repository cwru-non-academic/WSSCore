# Agent Notes (WSSCoreInterface)
This repository is a C# class library intended to be Unity-compatible. It targets `net48` and ships a low-level protocol client plus higher-level “core/layer/controller” wrappers.
No Cursor rules (`.cursor/rules/`, `.cursorrules`) and no Copilot instructions (`.github/copilot-instructions.md`) were found in this repo at the time this file was generated.

## Repo Layout
- `WSS_Core_Interface.sln` / `WSS_Core_Interface.csproj`: single-project solution.
- `CoreModule/`: transport + framing + protocol client + stimulation core.
- `CalibrationModule/`, `ModelModule/`: JSON-backed config layers/controllers.
- Build outputs often exist in-tree: `bin/` and `obj/`.

## Build / Test / Lint
### Prereqs
- .NET SDK that can build `net48` projects (modern .NET SDKs can).
- On Linux, serial support may require native deps; see `README.md` for `libudev` and `dialout` group notes.
### Build
```bash
dotnet restore "WSS_Core_Interface.sln"
dotnet clean "WSS_Core_Interface.sln" -c Release --nologo
dotnet build "WSS_Core_Interface.sln" -c Release --nologo
dotnet build "WSS_Core_Interface.sln" -c Debug --nologo
```
Artifact: `bin/Release/net48/WSS_Core_Interface.dll`
### Docs (Optional)
Strict XML-doc validation (useful before release):
```bash
dotnet build "WSS_Core_Interface.sln" -c Release --nologo -p:GenerateDocumentationFile=true -p:WarningsAsErrors=1591
```
DocFX note: avoid combining `/// <inheritdoc/>` with duplicated `<param>` tags; put extra info in `<remarks>` instead.
### Tests
There are currently no test projects in the solution, so `dotnet test` typically restores/builds and exits without running tests.
```bash
dotnet test "WSS_Core_Interface.sln" -c Release --nologo
```
If/when tests are added, use these patterns to run a single test:
```bash
# Run one test by fully-qualified name substring
dotnet test "path/to/SomeTests.csproj" --filter "FullyQualifiedName~Namespace.ClassName.TestName" --nologo

# Run one test by method name substring
dotnet test "path/to/SomeTests.csproj" --filter "Name~TestName" --nologo
```
### Lint / Formatting
No dedicated linting/format tooling (StyleCop/Analyzers/EditorConfig) is checked in.
Safe, optional commands (only if available in your environment):
```bash
# Requires dotnet-format tool; may not be installed/configured
dotnet format "WSS_Core_Interface.sln"
```
If you introduce analyzers or formatting rules, keep them compatible with Unity workflows and `net48`.
### VS Code
- Workspace settings exist under `.vscode/`. Prefer solution-wide `dotnet` commands targeting `WSS_Core_Interface.sln`.
- `.vscode/settings.json` currently points `dotnet.defaultSolution` at a different `.sln`; treat it as stale.

## Code Style (Observed Conventions)
### Namespaces / File Structure
- Namespaces are block-scoped (brace style), e.g. `namespace Wss.CoreModule { ... }`.
- Prefer one top-level type per file.
- XML doc comments (`/// <summary>`) are common on public APIs; match that style when adding public surface area.
- Place XML doc comments immediately above the documented symbol (and above any attributes like `[Flags]`).
- Avoid file-level `///` blocks that are not attached to a type/member (they become invalid XML docs).
- `#region` is used heavily in some files (notably `CoreModule/WssStimulationCore.cs`); follow local structure when editing.
- Avoid adding compile-time Unity dependencies; `CoreModule/Support/Log.cs` uses reflection to bind Unity logging when present.

### Using Directives / Imports
- Prefer `using` directives at the top of the file, outside the namespace.
- Avoid `using` directives inside the namespace block (one file currently does; do not copy that pattern).
- Keep `System.*` usings first, then third-party (`Newtonsoft.Json`), then project (`Wss.*`).
- Remove unused usings when you touch a file.

### Formatting
- 4-space indentation.
- Braces on new lines (Allman style):
```csharp
if (cond)
{
    DoThing();
}
```
- Prefer early returns for guard clauses.

### Types / Nullability
- Project does not enable nullable reference types globally (no `<Nullable>enable</Nullable>`).
- Be explicit about null handling anyway:
  - Guard public APIs against `null`/whitespace inputs (commonly `ArgumentException`).
  - When returning `null` is meaningful, document it.
- Avoid APIs not available on `net48` (Unity/older host compatibility).

### Naming
- Public types/members: `PascalCase`.
- Private fields: leading underscore, e.g. `_transport`, `_setupGate`.
- Interfaces: `IThing`.
- Local variables: `camelCase`.
- Constants: `PascalCase` or `const` with descriptive name (existing code uses both patterns; match the file you are editing).

### Async / Concurrency
- Async methods are `*Async` and usually return `Task`/`Task<T>`.
- Use `TaskCreationOptions.RunContinuationsAsynchronously` for `TaskCompletionSource` (see `CoreModule/WssClient.cs`).
- Use `ConfigureAwait(false)` inside library code when the continuation context should not be captured (pattern is already present).
- Prefer `SemaphoreSlim`/`ConcurrentDictionary` for cross-thread coordination as used in the client/core.
- For background loops, use `CancellationToken` and graceful shutdown; avoid `Thread.Abort` patterns.

### Error Handling / Logging
- Programming errors (bad arguments, invalid ranges): throw `ArgumentException` / `ArgumentOutOfRangeException`.
- Runtime/protocol/transport issues:
  - Return strings prefixed with `"Error:"` is a protocol pattern in `WssClient.ProcessFrame`; callers sometimes treat that as a failure signal.
  - Log warnings/errors via `CoreModule/Support/Log.cs` (`Log.Info/Warn/Error`).
- Don’t swallow exceptions silently unless the operation is best-effort cleanup; if you catch broadly, log enough context.

### Protocol / Transport Notes
- `CoreModule/WssClient.cs` correlates replies by `(target,msgId)` and supports multiple in-flight requests via a FIFO queue per key.
- `SendAwaitOneAsync` applies a ~2s timeout; cancellation/timeout typically manifests as `OperationCanceledException` in low-level code.
- Streaming paths often send fire-and-forget commands; setup/config paths are request/response.
- Device failures are often represented as `"Error: ..."` strings; higher layers may throw when they see that prefix.

### Public API Behavior
- `IStimulationCore` describes a non-blocking lifecycle:
  - `Initialize()` starts work.
  - `Tick()` advances a state machine.
  - Mutators enqueue work and return quickly.
- When adding new API methods, preserve this pattern: avoid blocking waits in public calls.

### JSON Config Controllers
- Controllers commonly accept either a file path or a directory; directory inputs get a default filename appended:
  - Model: `modelConfig.json`
  - Stim params: `stimParams.json`
- Dotted keys address JSON nodes (examples: `stim.ch.1.maxPW`, `calib.mode`). Validate keys and bounds before writing.

### Validation Patterns
- Validate array lengths and ranges before encoding payloads (see `WssClient` helpers `ToByteValidated` / `ToU16Validated`).
- Prefer centralized validators for repeated rules (e.g., key validation in config controllers).
- Use `WSSLimits` for protocol bounds; keep payload ordering consistent with the firmware expectations.

## Working With Generated/Build Artifacts
- Avoid editing `bin/` and `obj/` contents.
- When adding new files, keep them under source directories (`CoreModule/`, `CalibrationModule/`, `ModelModule/`).

## Platform Notes (Serial)
From `README.md` (important for Linux/WSL and Unity hosts):
- Install `libudev1` (and sometimes `libudev-dev`).
- Ensure user is in `dialout` (or distro equivalent) to access `/dev/tty*`.

## Referencing From Other Apps
- This repo builds a `net48` DLL; standalone apps can reference `bin/Release/net48/WSS_Core_Interface.dll`.
- Runtime dependencies (e.g., `Newtonsoft.Json`, `System.IO.Ports`, `System.Memory`) may need to be copied alongside the app if not resolved via NuGet.
- The project sets `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` to make redistribution easier via `bin/` outputs.

## Quick “Agent” Checklist
- Confirm build passes: `dotnet build "WSS_Core_Interface.sln" -c Release`.
- Keep Unity/`net48` compatibility in mind; avoid editing `bin/`/`obj/`.
- When touching protocol/client code, sanity-check timeouts, cancellation behavior, and `"Error:"` propagation.
