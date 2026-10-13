"""Package contract regressions using a completed universal payload; no hardware."""
import argparse
import os
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('package', type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
package = args.package.resolve()

def validate(path, expected_error=None):
    result = subprocess.run(['dotnet', 'run', '--project', str(root / 'tools/ArtifactBuild/ArtifactBuild.csproj'),
                             '--no-build', '--', 'validate', '--manifest', str(root / 'tools/ArtifactBuild/manifests/universal.json'),
                             '--package', str(path)], cwd=root, capture_output=True, text=True)
    if expected_error is None:
        assert result.returncode == 0, result.stdout + result.stderr
    else:
        assert result.returncode != 0 and expected_error in result.stderr, result.stdout + result.stderr

validate(package)
print('PASS: exact universal payload')
with tempfile.TemporaryDirectory(prefix='wss-package-contract-') as temporary:
    workspace = Path(temporary)
    def reject(name, mutate, error):
        fixture = workspace / name
        shutil.copytree(package, fixture)
        mutate(fixture)
        validate(fixture, error)
        print('PASS: reject ' + name)

    reject('missing-dbus', lambda p: (p / 'backends/linux/Tmds.DBus.dll').unlink(), 'Package file set differs')
    reject('unexpected-backend-placement', lambda p: shutil.copy2(p / 'backends/windows/WSS.Transport.BLE.Windows.dll', p / 'WSS.Transport.BLE.Windows.dll'), 'Package file set differs')
    reject('unexpected-winrt', lambda p: shutil.copy2(p / 'WSS.Transport.BLE.dll', p / 'backends/windows/WinRT.Runtime.dll'), 'Package file set differs')
    reject('duplicate-wss-identity', lambda p: shutil.copy2(p / 'WSS_Core_Interface.dll', p / 'backends/linux/WSS_Core_Interface.dll'), 'Package file set differs')
    reject('incompatible-managed', lambda p: shutil.copy2(p / 'WSS_Core_Interface.dll', p / 'backends/linux/Tmds.DBus.dll'), 'declared assembly identity or target framework')

    def corrupt_machine(p):
        dll = p / 'backends/windows/WssBleWindows.dll'
        data = bytearray(dll.read_bytes())
        pe = struct.unpack_from('<I', data, 0x3c)[0]
        struct.pack_into('<H', data, pe + 4, 0x14c)
        dll.write_bytes(data)
    reject('wrong-native-architecture', corrupt_machine, 'AMD64 PE32+ DLL')

    def corrupt_exports(p):
        dll = p / 'backends/windows/WssBleWindows.dll'
        data = dll.read_bytes()
        old = b'wss_ble_scan_start\x00'
        assert old in data
        replacement = b'wss_test_' + b'x' * (len(old) - 10) + b'\x00'
        assert len(replacement) == len(old)
        dll.write_bytes(data.replace(old, replacement))
    reject('test-only-export', corrupt_exports, 'ABI export set differs')

    def corrupt_elf(p):
        elf = p / 'runtimes/linux-x64/native/libSystem.IO.Ports.Native.so'
        data = bytearray(elf.read_bytes())
        struct.pack_into('<H', data, 18, 183)
        elf.write_bytes(data)
    reject('wrong-linux-native-architecture', corrupt_elf, 'ELF64')
    if os.name != 'nt':
        reject('case-colliding-duplicate', lambda p: shutil.copy2(p / 'WSS.Transport.BLE.dll', p / 'wss.transport.ble.dll'), 'Case-colliding duplicate')
    else:
        print('Case-sensitive duplicate filesystem case: NOT EXECUTED on Windows (Linux CI gate).')

print('Universal package rejection contracts: PASS')
