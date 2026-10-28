#import <Foundation/Foundation.h>
#import <CoreBluetooth/CoreBluetooth.h>
#include "wss_macos_ble.h"

@interface WSSMacContext : NSObject<CBCentralManagerDelegate, CBPeripheralDelegate>
@property(nonatomic, strong) dispatch_queue_t queue;
@property(nonatomic, strong) CBCentralManager* central;
@property(nonatomic, strong) CBPeripheral* peer;
@property(nonatomic, strong) CBCharacteristic* rx;
@property(nonatomic, strong) CBCharacteristic* tx;
@property(nonatomic, strong) CBUUID* serviceUUID;
@property(nonatomic, strong) CBUUID* rxUUID;
@property(nonatomic, strong) CBUUID* txUUID;
@property wss_mac_callback callback;
@property void* user;
@property uint64_t operation;
@property uint64_t session;
@property uint64_t scan;
@property uint32_t command;
@property BOOL ready;
@property BOOL closed;
@property uint64_t lastOperation;
@end

@implementation WSSMacContext
- (void)emit:(uint32_t)kind operation:(uint64_t)operation session:(uint64_t)session status:(int32_t)status data:(NSData*)data {
    if (!_closed && _callback) _callback(_user, kind, operation, session, status,
        _ready ? (uint32_t)[_peer maximumWriteValueLengthForType:CBCharacteristicWriteWithResponse] : 0,
        data.bytes, (uint32_t)data.length);
}
- (void)complete:(int32_t)status {
    uint64_t operation = _operation; _operation = 0;
    if (operation) [self emit:1 operation:operation session:_session status:status data:nil];
}
- (void)resetLink {
    _ready = NO;
    _peer.delegate = nil;
    if (_peer) [_central cancelPeripheralConnection:_peer];
    _peer = nil; _rx = nil; _tx = nil;
}
- (void)fail:(int32_t)status {
    [self resetLink]; [self complete:status];
    _central.delegate = nil; _central = nil;
}
- (void)centralManagerDidUpdateState:(CBCentralManager*)central {
    if (_closed || central != _central) return;
    if (_command == 1 && _operation && central.state != CBManagerStateUnknown && central.state != CBManagerStateResetting)
        [self complete:central.state == CBManagerStatePoweredOn ? 0 : central.state == CBManagerStateUnauthorized ? 8 : 6];
    else if (central.state != CBManagerStatePoweredOn && central.state != CBManagerStateUnknown && central.state != CBManagerStateResetting) {
        uint64_t session = _session; BOOL active = _ready;
        [_central stopScan]; _scan = 0;
        [self fail:central.state == CBManagerStateUnauthorized ? 8 : 6];
        if (active) [self emit:4 operation:0 session:session status:6 data:nil];
    }
}
- (void)centralManager:(CBCentralManager*)central didDiscoverPeripheral:(CBPeripheral*)peer advertisementData:(NSDictionary<NSString*,id>*)advertisement RSSI:(NSNumber*)rssi {
    if (_closed || central != _central || !_scan) return;
    NSString* name = advertisement[CBAdvertisementDataLocalNameKey] ?: peer.name ?: @"";
    // RSSI precedes the name; newline in the advertised name remains part of the name.
    NSString* text = [NSString stringWithFormat:@"%@\n%@\n%@", peer.identifier.UUIDString, rssi, name];
    [self emit:2 operation:0 session:_scan status:0 data:[text dataUsingEncoding:NSUTF8StringEncoding]];
}
- (void)centralManager:(CBCentralManager*)central didConnectPeripheral:(CBPeripheral*)peer {
    if (central != _central || peer != _peer || _command != 4 || !_operation) return;
    peer.delegate = self; [peer discoverServices:@[_serviceUUID]];
}
- (void)centralManager:(CBCentralManager*)central didFailToConnectPeripheral:(CBPeripheral*)peer error:(NSError*)error {
    if (central == _central && peer == _peer) [self fail:9];
}
- (void)centralManager:(CBCentralManager*)central didDisconnectPeripheral:(CBPeripheral*)peer error:(NSError*)error {
    if (central != _central || peer != _peer) return;
    uint64_t session = _session;
    [self fail:9]; [self emit:4 operation:0 session:session status:9 data:nil];
}
- (void)peripheral:(CBPeripheral*)peer didDiscoverServices:(NSError*)error {
    if (peer != _peer || _command != 4 || !_operation) return;
    if (error) { [self fail:9]; return; }
    for (CBService* service in peer.services) if ([service.UUID isEqual:_serviceUUID]) {
        [peer discoverCharacteristics:@[_rxUUID, _txUUID] forService:service]; return;
    }
    [self fail:9];
}
- (void)peripheral:(CBPeripheral*)peer didDiscoverCharacteristicsForService:(CBService*)service error:(NSError*)error {
    if (peer != _peer || _command != 4 || !_operation) return;
    if (error) { [self fail:9]; return; }
    for (CBCharacteristic* c in service.characteristics) {
        if ([c.UUID isEqual:_rxUUID] && (c.properties & CBCharacteristicPropertyWrite)) _rx = c;
        if ([c.UUID isEqual:_txUUID] && (c.properties & (CBCharacteristicPropertyNotify | CBCharacteristicPropertyIndicate))) _tx = c;
    }
    if (!_rx || !_tx) { [self fail:9]; return; }
    [peer setNotifyValue:YES forCharacteristic:_tx];
}
- (void)peripheral:(CBPeripheral*)peer didUpdateNotificationStateForCharacteristic:(CBCharacteristic*)characteristic error:(NSError*)error {
    if (peer != _peer || characteristic != _tx || _command != 4 || !_operation) return;
    if (error || !characteristic.isNotifying) { [self fail:9]; return; }
    _ready = YES; [self complete:0];
}
- (void)peripheral:(CBPeripheral*)peer didUpdateValueForCharacteristic:(CBCharacteristic*)characteristic error:(NSError*)error {
    if (peer != _peer || characteristic != _tx || !_ready) return;
    if (error) { uint64_t session = _session; [self fail:9]; [self emit:4 operation:0 session:session status:9 data:nil]; return; }
    [self emit:3 operation:0 session:_session status:0 data:characteristic.value];
}
- (void)peripheral:(CBPeripheral*)peer didWriteValueForCharacteristic:(CBCharacteristic*)characteristic error:(NSError*)error {
    if (peer != _peer || characteristic != _rx || _command != 5 || !_operation) return;
    if (error) [self fail:9]; else [self complete:0];
}
@end

uint32_t wss_mac_abi(void) { return 0x00010000; }
void* wss_mac_create(wss_mac_callback callback, void* user) {
    if (!callback) return NULL;
    WSSMacContext* context = [WSSMacContext new];
    context.queue = dispatch_queue_create("org.wss.corebluetooth", DISPATCH_QUEUE_SERIAL);
    context.callback = callback; context.user = user;
    return (__bridge_retained void*)context;
}
void wss_mac_destroy(void* handle) {
    if (!handle) return;
    WSSMacContext* context = (__bridge_transfer WSSMacContext*)handle;
    dispatch_sync(context.queue, ^{
        context.closed = YES; context.callback = NULL; context.operation = 0;
        [context.central stopScan]; [context resetLink];
        context.central.delegate = nil; context.central = nil;
    });
}
void wss_mac_cancel(void* handle, uint64_t operation) {
    if (!handle) return;
    WSSMacContext* c = (__bridge WSSMacContext*)handle;
    dispatch_async(c.queue, ^{
        if (c.closed || c.operation != operation) return;
        [c.central stopScan]; c.scan = 0; [c fail:4];
        // Abandon this manager/delegate epoch before any reconnect. Late callbacks
        // from the cancelled GATT operation can never complete a later command.
        c.central.delegate = nil; c.central = nil;
    });
}
int32_t wss_mac_submit(void* handle, uint32_t command, uint64_t operation, uint64_t session,
    const char* device, const char* service, const char* rx, const char* tx,
    const uint8_t* bytes, uint32_t length, uint32_t timeout_ms) {
    if (!handle || !operation || command < 1 || command > 6 || !timeout_ms || timeout_ms > INT32_MAX || (length && !bytes)) return 1;
    @try {
        NSString* deviceId = device ? [NSString stringWithUTF8String:device] : nil;
        CBUUID* serviceId = service ? [CBUUID UUIDWithString:[NSString stringWithUTF8String:service]] : nil;
        CBUUID* rxId = rx ? [CBUUID UUIDWithString:[NSString stringWithUTF8String:rx]] : nil;
        CBUUID* txId = tx ? [CBUUID UUIDWithString:[NSString stringWithUTF8String:tx]] : nil;
        NSUUID* uuid = deviceId ? [[NSUUID alloc] initWithUUIDString:deviceId] : nil;
        if ((command == 2 && (!serviceId || !session)) || (command == 4 && (!uuid || !serviceId || !rxId || !txId || !session))) return 1;
        NSData* data = [NSData dataWithBytes:bytes length:length];
        WSSMacContext* c = (__bridge WSSMacContext*)handle;
        __block int32_t result = 0;
        dispatch_sync(c.queue, ^{
            if (c.closed || operation <= c.lastOperation) { result = 2; return; }
            if (c.operation) { result = 10; return; }
            c.lastOperation = operation;
            c.operation = operation; c.command = command;
            // Queue the command so no completion is delivered inline to its owner.
            dispatch_async(c.queue, ^{
                if (c.closed || c.operation != operation) return;
                @try {
                    if (command == 1) {
                        if (!c.central) c.central = [[CBCentralManager alloc] initWithDelegate:c queue:c.queue options:@{CBCentralManagerOptionShowPowerAlertKey:@NO}];
                        if (c.central.state != CBManagerStateUnknown && c.central.state != CBManagerStateResetting)
                            [c complete:c.central.state == CBManagerStatePoweredOn ? 0 : c.central.state == CBManagerStateUnauthorized ? 8 : 6];
                    } else if (command == 6) {
                        [c resetLink]; [c complete:0];
                        c.central.delegate = nil; c.central = nil;
                    } else if (!c.central || c.central.state != CBManagerStatePoweredOn) [c complete:6];
                    else if (command == 2) {
                        if (c.scan || c.peer) { [c complete:2]; return; }
                        c.scan = session; [c.central scanForPeripheralsWithServices:@[serviceId] options:nil]; [c complete:0];
                    } else if (command == 3) {
                        [c.central stopScan]; c.scan = 0; [c complete:0];
                    } else if (command == 4) {
                        if (c.peer || c.scan) { [c complete:2]; return; }
                        NSArray<CBPeripheral*>* peers = [c.central retrievePeripheralsWithIdentifiers:@[uuid]];
                        if (!peers.count) { [c complete:9]; return; }
                        c.session = session; c.serviceUUID = serviceId; c.rxUUID = rxId; c.txUUID = txId;
                        c.peer = peers.firstObject; [c.central connectPeripheral:c.peer options:nil];
                    } else if (command == 5) {
                        if (!c.ready || c.session != session || length > [c.peer maximumWriteValueLengthForType:CBCharacteristicWriteWithResponse]) { [c complete:2]; return; }
                        [c.peer writeValue:data forCharacteristic:c.rx type:CBCharacteristicWriteWithResponse];
                    }
                } @catch (NSException* exception) { [c fail:9]; }
            });
            // Retaining c until this bounded deadline is intentional; destroy marks
            // it closed and fences callbacks before releasing the external owner.
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)timeout_ms * NSEC_PER_MSEC), c.queue, ^{
                if (!c.closed && c.operation == operation) {
                    [c.central stopScan]; c.scan = 0; [c fail:5];
                    c.central.delegate = nil; c.central = nil;
                }
            });
        });
        return result;
    } @catch (NSException* exception) { return 1; }
}
