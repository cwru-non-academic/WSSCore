#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p out
for arch in x86_64 arm64; do
  xcrun clang -arch "$arch" -mmacosx-version-min=11.0 -fobjc-arc -fvisibility=hidden \
    -Wall -Wextra -Wno-unused-parameter -dynamiclib native/Bridge.m \
    -framework Foundation -framework CoreBluetooth -install_name @rpath/libwss_macos_ble.dylib \
    -o "out/libwss_macos_ble-$arch.dylib"
done
xcrun lipo -create out/libwss_macos_ble-x86_64.dylib out/libwss_macos_ble-arm64.dylib -output out/libwss_macos_ble.dylib
codesign --force --sign - out/libwss_macos_ble.dylib
python3 verify_native.py out/libwss_macos_ble.dylib
