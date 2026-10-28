# Reviewed macOS BLE receiver migration

This feature is separate from the held Core reliability/channel candidate. Its PR
produces an immutable candidate; opening a Core PR does not dispatch downstream
promotion. Existing automatic handlers require a successful canonical **dev push**,
and downstream dependency PRs still require human review/merge. Do not merge this
feature into dev until the receiving contracts are ready. MATLAB is owned by its
separate worker and is not changed here.

## Contract delta

The exact universal inventory adds only:

* `backends/macos/WSS.Transport.BLE.MacOS.dll` — identity
  `WSS.Transport.BLE.MacOS`, version `1.0.0.0`, `.NETStandard,Version=v2.0`.
* `backends/macos/libwss_macos_ble.dylib` — universal x86_64/arm64 Mach-O dylib,
  ABI `0x00010000`, production exports `wss_mac_abi`, `wss_mac_create`,
  `wss_mac_destroy`, `wss_mac_submit`, `wss_mac_cancel`.

No new NuGet runtime dependencies. Copy rules must retain `.dylib` recursively.
Backend platform key is `macos`; factory/bootstrap types are
`Wss.Transports.Backends.MacOS.MacBleNusBackendFactory` and
`Wss.Transports.Backends.MacOS.MacBleBackend`. Missing either declared backend file,
wrong architecture/export/ABI or failed managed activation must fail compatibility.
There is no unconditional macOS BLE skip or success fallback.

Core's producer adds these exact required job names to the existing names:

* `Build macOS universal BLE bridge`
* `BLE consumer (macOS arm64)`
* `BLE consumer (macOS x86_64)`

The universal ZIP name, artifact name, producer tuple, SHA-256 and provenance remain
unchanged. Core's Python and Unity handoff preparations require the added jobs.
Receiver producer-proof validators should add the same names for this candidate.
Run-level success remains required; matching a subset of green jobs is insufficient.

## Prepare before candidate promotion

Each integration and application receiver needs a separate reviewed preparatory
CI/schema PR because a dependency-only promotion PR cannot safely modify CI and
the dependency simultaneously under existing restricted-change rules. Agree branch
ownership with the orchestrating parent before editing another repository.
If supporting the previous payload during transition, distinguish its exact old
inventory from the exact new inventory; require all new macOS gates whenever the
new payload is declared. Never loosen the allowlist to arbitrary extra files.

| Receiver | Preparatory work and ownership boundary |
| --- | --- |
| C# integration / C# CLI | Exact inventory and provenance validators, recursive dylib copy, packaged facade/native ABI activation on macOS; keep detailed protocol tests in Core. |
| Python integration | Exact inventory/copy rules, CLR facade activation and C ABI load through the candidate, require Core's macOS job names. Prepare before candidate preflight. |
| Python CLI | Consume pinned integration candidate, retain dylib, exercise BLE command routing and packaged loading on macOS; no peripheral writes. First complete application traversal. |
| Unity integration / GUI | Retain native universal plugin, platform import rules, explicit bootstrap and real Unity loader/domain lifecycle check; do not infer Unity compatibility from .NET 9. |
| MATLAB integration / CLI | Coordinate with MATLAB owner after runtime/license work; native search path and actual supported CLR/host must be proved. Keep propagation gated until ready. |
| ROS Python application | Match Python receiver schema/asset rules and macOS packaged compatibility; ROS support/setup constraints remain explicit. Hold broader traversal for this first Python-CLI walk. |

## Acceptance order

1. Review Core source/tests and obtain green canonical producer gates for the draft
   PR, including both macOS slices; retain the run/artifact/checksum tuple.
2. Review/prepare Python integration and CLI receiver CI/schema separately. Do not
   edit MATLAB-owned branches. Broader receiver preparation can be reviewed without
   dispatching their propagation.
3. Use the existing candidate-preflight entry points with the exact successful Core
   tuple, after their validators explicitly accept this reviewed feature candidate.
   Current automatic handlers accept dev pushes only; do not forge a dev producer,
   weaken provenance, or merge solely to manufacture a trigger.
4. Walk the real payload through Python integration → Python CLI; record candidate
   preflight and normal dependency PR check links. No automatic approval or merge.
5. After MATLAB is fully working and the receiver preparatory changes are reviewed,
   release the held larger candidate through the full tree. Require macOS BLE
   compatibility at every receiving boundary. Physical BLE validation remains a
   separate real-Mac/peripheral acceptance item throughout.
