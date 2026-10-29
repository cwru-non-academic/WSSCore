"""Validate the actual universal production bridge without opening Bluetooth."""
import ctypes
import subprocess
import sys
import threading
from pathlib import Path

path = Path(sys.argv[1]).resolve()
assert set(subprocess.check_output(['lipo', '-archs', str(path)], text=True).split()) == {'arm64', 'x86_64'}
expected = {'_wss_mac_abi', '_wss_mac_create', '_wss_mac_destroy', '_wss_mac_submit', '_wss_mac_cancel'}
for arch in ('arm64', 'x86_64'):
    exports = subprocess.check_output(['nm', '-arch', arch, '-gU', str(path)], text=True)
    assert {line.split()[-1] for line in exports.splitlines() if line.strip()} == expected, exports
lib = ctypes.CDLL(str(path))
lib.wss_mac_abi.restype = ctypes.c_uint32
assert lib.wss_mac_abi() == 0x10000
callback_type = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint64,
                               ctypes.c_uint64, ctypes.c_int32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32)
events = []
completed = threading.Event()
def receive(*args):
    events.append(args)
    completed.set()
callback = callback_type(receive)
lib.wss_mac_create.argtypes = [callback_type, ctypes.c_void_p]
lib.wss_mac_create.restype = ctypes.c_void_p
lib.wss_mac_destroy.argtypes = [ctypes.c_void_p]
context = lib.wss_mac_create(callback, None)
assert context
assert not events, 'Create must neither initialize Bluetooth nor callback'
lib.wss_mac_submit.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint64,
    ctypes.c_uint64, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p,
    ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32]
lib.wss_mac_submit.restype = ctypes.c_int32
assert lib.wss_mac_submit(context, 0, 1, 0, None, None, None, None, None, 0, 1000) == 1
# Disconnecting a never-initialized context exercises real native dispatch and
# callback layout without creating CBCentralManager or requesting authorization.
assert lib.wss_mac_submit(context, 6, 1, 0, None, None, None, None, None, 0, 1000) == 0
assert completed.wait(3), 'Native completion callback did not arrive'
lib.wss_mac_destroy(context)
assert len(events) == 1 and events[0][1:6] == (1, 1, 0, 0, 0), events
print('PASS: universal x86_64/arm64 exact production exports, ABI 1.0, load/create/dispatch/callback/destroy; no Bluetooth initialization')
