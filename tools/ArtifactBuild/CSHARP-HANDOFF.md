# C# and Unity consumer integration

Use **one** `WSS-Universal-<TAG>.zip` and `SHA256SUMS.txt`. Verify the checksum,
extract once, and use the nested `WSS-Universal/` directory as the artifact root.
The complete inventory and deployment contract are in [README.md](README.md).

## C# implementation library and applications

1. Use one artifact root for project references and redistribution. If multiple
   existing artifact-directory properties remain, give them the same root and
   reject differing inputs; no separate platform downloads are required.
2. Reference `WSS_Core_Interface.dll`, `WSS.Transport.Serial.dll`, and
   `WSS.Transport.BLE.dll` from that root, copy-local. Serial types belong directly
   to `WSS.Transport.Serial`; do not resolve them through BLE type forwarding.
3. Replace Windows backend references with `WSS.Transport.BLE.Windows.dll`.
   For explicit registration use
   `Wss.Transports.Backends.Windows.WindowsBleBackend.Register()`.
   Automatic loading needs no registration call when the expected layout is deployed.
4. Keep `WSS.Transport.BLE.Windows.Native.dll` and production `WssBleWindows.dll`
   beside the Windows adapter. Preserve the Linux backend and all its dependencies,
   and both platform runtime trees in library/installer redistributable content.
5. Remove obsolete Windows adapters, managed WinRT/SDK projections, Drawing,
   Windows.Extensions, and SystemEvents dependencies from the WSS payload. Do not
   remove the shared dependencies listed in the universal manifest.
6. Use System.IO.Ports NuGet **8.0.0** for runnable .NET runtime selection. Keep
   `runtimes/{win|unix}/lib/net8.0/System.IO.Ports.dll` and the Linux-x64 native
   asset in redistributed content. Do not overwrite the host-selected Ports DLL
   with the root net462 alternate; preserve the complete original artifact separately.
7. Update copy-to-output/publish rules, required-file lists, loading tests, and
   workflows to this one input. Assert ordinary shared loading and one identity
   each for Core/Serial/BLE. Never load the native DLL as a managed assembly.
8. Rebuild the implementation library, applications, tests, and dependent binaries.
   Existing facade constructors/options and stimulation-layer logic need no rewrite.

## Unity

1. Replace WSS managed plugins and assembly-definition references with the final
   Core/Serial/BLE set and `WSS.Transport.BLE.Windows.dll`; retain the native wrapper
   and production native DLL. Remove obsolete adapter/WinRT assemblies and duplicate
   shared DLLs rather than overlaying new files on an old plugin set.
2. Update explicit Windows registration to
   `Wss.Transports.Backends.Windows.WindowsBleBackend.Register()` after preloading
   the adapter, or deploy the automatic loader's backend layout under the host base directory.
3. Reference Serial directly wherever its types are used. Rebuild scripts and
   dependent assemblies; removed aliases are not a binary compatibility contract.
4. Configure `WssBleWindows.dll` for Windows Editor/Standalone x86_64 only, not
   Any Platform. Ensure native resolution and the wrapper's required dependencies
   are available. Use a .NET Standard-compatible Unity API level.
5. Preserve the complete universal input for redistribution even when Unity importer
   settings select platform-specific plugins. No platform-specific ZIP is needed.

Validate Editor and player assembly/native loading, explicit registration where used,
and communication with an approved non-actuating test device. Prior simulator evidence
does not establish renamed-assembly compatibility. Mono success does not establish
IL2CPP/AOT support; callback preservation and stripping need separate validation.

Dispose owned BLE clients/transports before assembly reload and application shutdown;
an unloaded domain is not a substitute for callback fencing. This handoff does not
authorize external repository changes or investigation of host shutdown behavior.
