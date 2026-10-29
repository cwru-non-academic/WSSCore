# macOS CoreBluetooth BLE bridge

The production Objective-C bridge uses CBCentralManager/CBPeripheral to scan for
the configured service, connect by CoreBluetooth UUID, discover the required
write-with-response RX and notify/indicate TX characteristics, enable notifications,
and acknowledge writes. It is not a mock backend. The `IBleNusBackend` adapter is
`WSS.Transport.BLE.MacOS`, targeting .NET Standard 2.0 like Core and the other backends.

## Build and distribution

Run `bash tools/MacBleBridge/build.sh` on a Mac with Xcode command-line tools,
Python 3 and the macOS SDK. The minimum native deployment target is macOS 11.0.
Clang builds x86_64 and arm64; lipo combines them and codesign applies an ad-hoc
signature. No paid Apple account, signing service or runner is needed for CI.
The resulting file is `out/libwss_macos_ble.dylib`; generated binaries are never
committed. `verify_native.py` checks both architectures and their exact public C
exports, loads the host slice, checks ABI 1.0, and creates/destroys a context without
initializing Bluetooth. The header documents borrowed data and callback fencing.

`Build macOS universal BLE bridge` uploads only that dylib. `Build and validate
artifacts` downloads the **same workflow run's** native artifact, builds the managed
adapter, checks exact closure/identities and Mach-O architecture headers, then
creates the single universal ZIP/checksum. All consumers download that ZIP; no
second archive is rebuilt for macOS. Public standard `macos-14` (arm64) and
`macos-15-intel` runners run mandatory packaged ABI/load tests on .NET 8 and 9.
Publication and routine propagation wait for both `BLE consumer (macOS arm64)`
and `BLE consumer (macOS x86_64)`. Existing Core conformance remains authoritative.

## Runtime and permissions

Deploy the complete universal tree, including:

```
backends/macos/WSS.Transport.BLE.MacOS.dll
backends/macos/libwss_macos_ble.dylib
```

The host must use a 64-bit runtime with a matching x86_64 or arm64 slice. .NET 8/9
packaged loading is covered by CI. Unity Mono, MATLAB's CLR hosting and other
runtime loaders need separate receiver validation before claiming compatibility.
Automatic facade selection uses `macos`; explicit hosts may call
`Wss.Transports.Backends.MacOS.MacBleBackend.Register()` before constructing a BLE
transport. Construction/registration alone does not open Bluetooth. A native probe
or connection initializes CoreBluetooth and can request user authorization.

The **host application** owns TCC permission, not the dylib. Supply an appropriate
`NSBluetoothAlwaysUsageDescription` in the application's Info.plist; sandboxed
applications also need the Bluetooth entitlement
`com.apple.security.device.bluetooth`. A CLI launched by Terminal, Python, MATLAB
or an editor may be attributed to that host by macOS; authorize the actual host in
System Settings → Privacy & Security → Bluetooth. Signed/hardened applications
must sign embedded code consistently with their own distribution requirements.
CI's ad-hoc signature does not establish notarization or a production signing policy.
Denied permission, an unavailable adapter or powered-off Bluetooth is an error,
not successful runtime evidence. DeviceId is the OS's peripheral UUID, not a MAC
address; scan by configured name to obtain an identifier on that Mac.

References: [Apple Core Bluetooth](https://developer.apple.com/documentation/corebluetooth),
[CBCentralManager](https://developer.apple.com/documentation/corebluetooth/cbcentralmanager),
[standard hosted runner labels](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

## Lifecycle and evidence limits

All CoreBluetooth state and delegate callbacks run on a private serial dispatch
queue. Operation IDs and connection/scan epochs are monotonic. Native inputs are
copied synchronously; managed callbacks copy notification data before returning.
User handlers run on a managed notification pump outside native and lifecycle
locks. Disconnect/destruction fence callbacks and stale queued delivery; a handler
may synchronously disconnect itself. One previously claimed user callback may
finish after disconnect. Dispose before unloading the host/domain.

Writes snapshot caller data, serialize complete messages and chunk to the current
write-with-response budget. A submitted failure/cancellation ends the connection;
payloads are never replayed. Cancellation and the 10-second operation deadline
abandon the manager epoch so late GATT callbacks cannot complete a new operation.
Discovery is service-filtered; auto-selection also validates both characteristics
before the facade ranks compatible candidates by RSSI. Discovery results and
notification queues are bounded at 256. Notification overflow ends the session.

Ordinary CI proves native compilation, both binary slices, exact exports, ABI
loading, managed construction/registration, and deterministic simulated adapter
contracts (chunk order/snapshots, cancellation without replay, stale notification
rejection, reentrant disconnect and disposal). It does **not** initialize a real
CBCentralManager, prove TCC behavior, discover/connect a physical peripheral, or
write stimulation data. The fake is confined to managed tests and is never packaged.
The CoreBluetooth delegate/GATT interaction still requires real Mac validation.

Before claiming physical BLE support validated, test on Intel and Apple Silicon
with an authorized host and a safe non-stimulating NUS test peripheral: scan/name
and UUID selection; missing/wrong characteristic rejection; notifications and write
acknowledgments; MTU budget changes; radio off/permission denial; cancellation,
link loss and reconnect; host shutdown/domain reload. Do not use stimulation
hardware as an unattended CI target.
