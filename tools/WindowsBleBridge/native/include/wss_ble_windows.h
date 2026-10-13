#pragma once
#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#define WSS_CALL __cdecl
#ifdef WSS_BLE_EXPORTS
#define WSS_API __declspec(dllexport)
#else
#define WSS_API
#endif
#else
#define WSS_CALL
#define WSS_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* ABI 1.0, Windows x64, little endian, pack 8. No platform objects cross this ABI.
   All inputs are borrowed for the duration of the call only. Callback structures,
   names (UTF-8, NOT null terminated), and UUID arrays are borrowed ONLY until the
   callback returns. UUID bytes use canonical textual/network order, not GUID memory
   order. Callback must copy data, return promptly, and must NOT call destroy.
   Every accepted command has one completion, unless destroy suppresses it.
   Destroy cancels outstanding work WITHOUT further callbacks. It is a synchronous
   callback fence. Null destroy is a no-op; a non-null handle must be destroyed once.
   Operations may be submitted concurrently; destruction must be serialized with
   submissions by the owner. IDs must be nonzero and unique while outstanding.
   Scan session IDs must be nonzero and never reused within a context.
   Cancel requests cancellation; a completion already committed can win the race.
   A successful stop completion fences all native advertisements for that session.
   No exceptions may escape the callback or exported entry points. */
typedef void* wss_ble_context;
enum wss_ble_status {
    WSS_OK = 0, WSS_INVALID_ARGUMENT = 1, WSS_INVALID_STATE = 2,
    WSS_UNSUPPORTED_ABI = 3, WSS_CANCELLED = 4, WSS_TIMED_OUT = 5,
    WSS_ADAPTER_UNAVAILABLE = 6, WSS_BLUETOOTH_DISABLED = 7,
    WSS_ACCESS_DENIED = 8, WSS_NATIVE_FAILURE = 9, WSS_BUSY = 10,
    WSS_OVERFLOW = 11, WSS_CLOSING = 12
};
enum wss_ble_event_kind { WSS_COMPLETION = 1, WSS_ADVERTISEMENT = 2,
    WSS_SCAN_TERMINATED = 3, WSS_SCAN_OVERFLOW = 4,
    WSS_GATT_COMPLETION = 5, WSS_NOTIFICATION = 6, WSS_CONNECTION_TERMINATED = 7 };
enum wss_ble_gatt_stage { WSS_RESOLVE_DEVICE = 1, WSS_DISCOVER_SERVICE = 2,
    WSS_DISCOVER_RX = 3, WSS_DISCOVER_TX = 4, WSS_ENABLE_NOTIFY = 5,
    WSS_WRITE = 6, WSS_DISABLE_NOTIFY = 7, WSS_LINK_LOSS = 8 };
enum wss_ble_adapter_state { WSS_ADAPTER_UNKNOWN = 0, WSS_ADAPTER_ABSENT = 1,
    WSS_ADAPTER_NO_LE = 2, WSS_ADAPTER_READY = 3, WSS_ADAPTER_OFF = 4 };
enum wss_ble_address_type { WSS_ADDRESS_UNSPECIFIED = 0,
    WSS_ADDRESS_PUBLIC = 1, WSS_ADDRESS_RANDOM = 2 };
#pragma pack(push, 8)
typedef struct wss_ble_abi_info {
    uint32_t struct_size, major, minor, pointer_size;
    uint32_t event_size, advertisement_size, capabilities, reserved;
} wss_ble_abi_info;
typedef struct wss_ble_advertisement {
    uint32_t struct_size, address_type;
    uint64_t address;
    int32_t rssi;
    uint32_t rssi_valid;
    const uint8_t* name;
    uint32_t name_length, uuid_count;
    const uint8_t* uuids; /* uuid_count contiguous 16-byte UUIDs */
} wss_ble_advertisement;
typedef struct wss_ble_event {
    uint32_t struct_size, kind;
    uint64_t operation_id, session_id;
    int32_t status, hresult;
    uint32_t bluetooth_error, adapter_state;
    const wss_ble_advertisement* advertisement;
    uint64_t dropped_count;
} wss_ble_event;
/* ABI 1.1, capability 0x2. All pointers are borrowed only during the call.
   selector_kind: 1 = address, 2 = Windows ID (strict UTF-8, no embedded NUL).
   timeout_ms: 1..INT32_MAX. UUIDs are canonical/network order. reserved = 0. */
typedef struct wss_ble_nus_connect_options {
    uint32_t struct_size, selector_kind, address_type, timeout_ms;
    uint64_t address;
    const uint8_t* device_id_utf8;
    uint32_t device_id_length, reserved;
    uint8_t service_uuid[16], rx_uuid[16], tx_uuid[16];
} wss_ble_nus_connect_options;
/* Kinds 5..7 ONLY use this envelope; base.struct_size is 88, session_id is
   connection_id. Scan envelopes remain 56 bytes. Unknown GATT/ATT = UINT32_MAX.
   data belongs to native until callback return. No characteristic objects escape. */
typedef struct wss_ble_gatt_event {
    wss_ble_event base;
    uint32_t stage, gatt_status, protocol_error, max_write_length;
    const uint8_t* data;
    uint32_t data_length, reserved;
} wss_ble_gatt_event;
#pragma pack(pop)
typedef void (WSS_CALL *wss_ble_callback)(void* user, const wss_ble_event* event);
WSS_API int32_t WSS_CALL wss_ble_get_abi_info(wss_ble_abi_info* info);
WSS_API int32_t WSS_CALL wss_ble_create(uint32_t abi_major, wss_ble_callback callback,
    void* user, wss_ble_context* context);
WSS_API int32_t WSS_CALL wss_ble_initialize(wss_ble_context context, uint64_t operation_id);
WSS_API int32_t WSS_CALL wss_ble_query_adapter(wss_ble_context context, uint64_t operation_id);
WSS_API int32_t WSS_CALL wss_ble_scan_start(wss_ble_context context, uint64_t operation_id, uint64_t session_id);
WSS_API int32_t WSS_CALL wss_ble_scan_stop(wss_ble_context context, uint64_t operation_id, uint64_t session_id);
WSS_API int32_t WSS_CALL wss_ble_cancel(wss_ble_context context, uint64_t operation_id);
WSS_API int32_t WSS_CALL wss_ble_destroy(wss_ble_context context);
/* Connection IDs increase strictly within a context, independently of scan IDs.
   Success means accepted. One GATT completion per accepted operation, except destroy.
   Connect includes uncached discovery and CCCD activation. Disconnect completion
   fences notifications even if CCCD disable failed. A submitted cancelled/timed-out
   write tears down its connection; reconnect is Busy until that write is terminal.
   Payloads are never chunked/replayed. Ordinary write budget is current MTU - 3. */
WSS_API int32_t WSS_CALL wss_ble_nus_connect(wss_ble_context context,
    uint64_t operation_id, uint64_t connection_id, const wss_ble_nus_connect_options* options);
WSS_API int32_t WSS_CALL wss_ble_nus_write(wss_ble_context context,
    uint64_t operation_id, uint64_t connection_id, const uint8_t* bytes,
    uint32_t length, uint32_t timeout_ms);
WSS_API int32_t WSS_CALL wss_ble_nus_disconnect(wss_ble_context context,
    uint64_t operation_id, uint64_t connection_id, uint32_t timeout_ms);
#ifdef __cplusplus
}
static_assert(sizeof(wss_ble_abi_info) == 32, "ABI info layout");
#ifdef _WIN64
static_assert(sizeof(wss_ble_event) == 56, "Event layout");
static_assert(sizeof(wss_ble_advertisement) == 48, "Advertisement layout");
static_assert(sizeof(wss_ble_nus_connect_options) == 88, "NUS options layout");
static_assert(sizeof(wss_ble_gatt_event) == 88, "GATT event layout");
static_assert(offsetof(wss_ble_nus_connect_options, service_uuid) == 40, "NUS UUID offset");
static_assert(offsetof(wss_ble_gatt_event, stage) == 56, "GATT extension offset");
static_assert(offsetof(wss_ble_gatt_event, data) == 72, "GATT data offset");
static_assert(offsetof(wss_ble_event, advertisement) == 40, "Event pointer offset");
static_assert(offsetof(wss_ble_advertisement, name) == 24, "Name pointer offset");
#endif
#endif
