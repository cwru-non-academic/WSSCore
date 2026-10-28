#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define WSS_MAC_API __attribute__((visibility("default")))
/* ABI 1.0: 64-bit macOS, cdecl. All strings are NUL-terminated UTF-8 inputs.
 * Callback bytes are borrowed until callback return; copy them and never invoke
 * user code or destroy from a callback. Accepted commands complete exactly once
 * unless destroy suppresses them. Destroy synchronously fences callbacks.
 * Owner serializes submissions with destroy. IDs are nonzero, never reused.
 * kind: 1 completion, 2 advertisement (UTF-8 UUID + newline + name),
 * 3 notification, 4 link loss. budget is write-with-response maximum length.
 * status: 0 success, 1 invalid argument, 2 invalid state, 4 cancelled,
 * 5 timeout, 6 unavailable/off, 8 denied, 9 native failure, 10 busy, 11 overflow.
 * Cancelled/timed-out writes disconnect; no bytes are retried or replayed.
 */
typedef void (*wss_mac_callback)(void*, uint32_t, uint64_t, uint64_t,
    int32_t, uint32_t, const uint8_t*, uint32_t);
WSS_MAC_API uint32_t wss_mac_abi(void);
WSS_MAC_API void* wss_mac_create(wss_mac_callback callback, void* user);
WSS_MAC_API void wss_mac_destroy(void* context);
/* command: 1 initialize/query adapter, 2 start scan (service), 3 stop scan,
 * 4 connect (UUID, service, RX, TX), 5 write, 6 disconnect.
 * Inputs are copied before returning. timeout_ms 1..INT32_MAX. */
WSS_MAC_API int32_t wss_mac_submit(void* context, uint32_t command, uint64_t operation,
    uint64_t session, const char* device, const char* service, const char* rx,
    const char* tx, const uint8_t* bytes, uint32_t length, uint32_t timeout_ms);
WSS_MAC_API void wss_mac_cancel(void* context, uint64_t operation);
#ifdef __cplusplus
}
#endif
